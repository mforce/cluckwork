namespace Cluckwork.Infrastructure.RateLimiting;

using System.Threading.RateLimiting;
using Cluckwork.Infrastructure.SharedState;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

// #544 — the public IRateLimiterPolicy the Api layer registers for each IP-keyed auth
// policy (login / refresh / client-errors). It is the ONLY bridge across the assembly
// boundary: the internal IFixedWindowCounter port stays internal to Infrastructure and is
// resolved here from request services, so nothing about the shared-state contract leaks to
// Cluckwork.Api. Same shape #545 (the account-keyed report cap) will reuse.
//
// OnRejected is null on purpose: the single global RateLimiterOptions.OnRejected handler in
// CluckworkRateLimitingServiceCollectionExtensions owns the 429 body, the Retry-After header,
// and the auth-only SecurityEvents.RateLimitRejected event. A per-policy OnRejected here
// would double-handle or split that logic.
public sealed class DistributedIpFixedWindowPolicy : IRateLimiterPolicy<string>
{
    private readonly string _keyPrefix;
    private readonly int _permitLimit;
    private readonly TimeSpan _window;

    public DistributedIpFixedWindowPolicy(string keyPrefix, int permitLimit, TimeSpan window)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPrefix);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(permitLimit);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        _keyPrefix = keyPrefix;
        _permitLimit = permitLimit;
        _window = window;
    }

    // Null on purpose: the single global RateLimiterOptions.OnRejected handler owns the 429
    // body, the Retry-After header, and the auth-only RateLimitRejected security event. That the
    // framework falls back to the global handler when a policy's OnRejected is null is guarded by
    // DistributedRateLimiterWiringTests.No_attacker_supplied_dimension_in_key_or_log (it asserts
    // the event fires); a runtime change to that behaviour fails there by name, not silently.
    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected => null;

    public RateLimitPartition<string> GetPartition(HttpContext httpContext)
    {
        var key = $"{_keyPrefix}:{RateLimitKey.ForClient(httpContext.Connection.RemoteIpAddress)}";
        var services = httpContext.RequestServices;

        // Resolve the singletons inside the partition factory so they are fetched only on a
        // cache miss (first request per key), not on every GetPartition call.
        //
        // The policy prefix is part of the COUNTER key. All three IP-keyed policies
        // (login / refresh / client-errors) share ONE injected IFixedWindowCounter, so a key
        // of the IP alone collapses their three budgets into one bucket per IP — refresh would
        // 429 on login's traffic, defeating the #143 NAT-starvation guard that makes them
        // separate. Keying on the POLICY name keeps them independent, while routes that
        // deliberately share a budget (login / step-up / change-password all use
        // LoginPolicyName, #273) still land in the same bucket by design.
        return RateLimitPartition.Get(key, k =>
            new DistributedIpFixedWindowRateLimiter(
                services.GetRequiredService<IFixedWindowCounter>(),
                services.GetRequiredService<TimeProvider>(),
                k, _permitLimit, _window));
    }
}
