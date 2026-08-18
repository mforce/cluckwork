namespace Cluckwork.Infrastructure.RateLimiting;

using Cluckwork.Application.Common;
using Cluckwork.Infrastructure.SharedState;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

// #545 — per-account report concurrency cap enforced through the shared lease
// backends (#543), replacing the process-local PartitionedRateLimiter (#311).
//
// A counting semaphore of PermitLimit per account, built from single-holder
// ILease slots: keys "report-cc:{accountId}:{slot}" for slot in [0, PermitLimit).
// AcquireAsync scans the slots and takes the first free one with a fresh owner
// token; all slots live => refused (the filter returns 429). A dead holder's
// slot is reclaimed by the lease TTL — never a bare counter (issue #545).
//
// PINNED failover (deliberately NOT the per-call ResilientLease, which #545
// deleted): the permit is bound to the concrete backend that granted it and
// renews/releases there for its whole lifetime. A per-call decorator silently
// evicts a live holder — acquire lands in-process during a Redis outage, Redis
// recovers, and a renew that re-probes Redis finds no key, returns false with no
// alarm, and lets the still-running report's slot be stolen at TTL. Pinning to
// in-process keeps that permit alive until the report finishes. A permit pinned
// to Redis whose Redis later throws degrades to over-admission only (bounded,
// self-healed by Redis's own TTL) — the tolerable direction.
//
// FAILURE POLICY (#545): capacity protection, so it must NEVER fail open. A
// Redis fault at acquire falls back to the in-process backend (a local
// per-instance ceiling) and alarms; it never admits on error. Only Redis faults
// are caught — any other exception propagates (fail-closed 500 for that request).
// A free slot granted to an owner, pinned to the backend that granted it. Returned
// by the scan and adopted by a permit when it re-acquires after a lease loss (#545).
internal readonly record struct SlotGrant(ILease Backend, string Key);

public sealed class DistributedReportConcurrencyLimiter
{
    internal const string Capability = "report-concurrency";

    private readonly ILease? _redis;
    private readonly ILease _fallback;
    private readonly TimeProvider _clock;
    private readonly ILogger<DistributedReportConcurrencyLimiter> _logger;
    private readonly int _permitLimit;
    private readonly TimeSpan _ttl;
    private readonly TimeSpan _renewInterval;

    internal DistributedReportConcurrencyLimiter(
        ILease? redis,
        ILease fallback,
        TimeProvider clock,
        ILogger<DistributedReportConcurrencyLimiter> logger,
        int permitLimit,
        TimeSpan ttl,
        TimeSpan renewInterval)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(permitLimit);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ttl, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(renewInterval, TimeSpan.Zero);
        _redis = redis;
        _fallback = fallback;
        _clock = clock;
        _logger = logger;
        _permitLimit = permitLimit;
        _ttl = ttl;
        _renewInterval = renewInterval;
    }

    // Scans the account's slots and takes the first free one, pinning the permit
    // to the backend that granted it. Returns null when all PermitLimit slots are
    // held by live leases (the caller returns 429). Synchronous under the hood —
    // ILease is a sync port (#543); the ValueTask keeps the endpoint filter's
    // await-shape and room to go async later. Checks the token between slots so a
    // disconnected client does not burn all N round trips.
    public ValueTask<ReportConcurrencyPermit?> AcquireAsync(
        Guid accountId, CancellationToken cancellationToken = default)
    {
        var owner = Guid.NewGuid().ToString("N");
        var grant = TryScan(accountId, owner, cancellationToken);
        if (grant is { } g)
        {
            var permit = new ReportConcurrencyPermit(
                g.Backend, g.Key, owner, _clock, _logger, _ttl, _renewInterval,
                // Re-acquire path for a lease loss: re-scan the same account's slots.
                // No request token here — a re-count must not be tied to the caller.
                reacquire: () => TryScan(accountId, owner, CancellationToken.None));
            return new ValueTask<ReportConcurrencyPermit?>(permit);
        }

        return new ValueTask<ReportConcurrencyPermit?>((ReportConcurrencyPermit?)null);
    }

    // Scans the account's slots for a free one, pinning to the granting backend.
    // Shared by the initial acquire and a permit's re-acquire-after-lease-loss path.
    private SlotGrant? TryScan(Guid accountId, string owner, CancellationToken cancellationToken)
    {
        // Once Redis faults during THIS scan, skip it for the remaining slots — one
        // fault means Redis is unreachable for this call, so re-probing every slot
        // only multiplies latency and alarm log volume under a sustained outage.
        var redisFaulted = false;
        for (var slot = 0; slot < _permitLimit; slot++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = $"report-cc:{accountId:N}:{slot}";
            if (TryAcquirePinned(key, owner, ref redisFaulted, out var backend))
                return new SlotGrant(backend, key);
        }

        return null;
    }

    // Tries Redis first when configured; a Redis FAULT (not a held slot) alarms
    // and falls back to the in-process ceiling for THIS slot. A reachable Redis
    // that reports the slot held returns false WITHOUT falling back — the scan
    // moves to the next slot. `backend` is only meaningful when this returns true.
    // The catch is deliberately NARROW here (unlike renew/release): at acquire time
    // an UNEXPECTED, non-Redis fault must fail CLOSED — propagate and 500 — because
    // nothing has been granted yet and denying the request is the safe direction.
    // A Redis fault (reachable-but-erroring or unreachable) falls back to the bounded
    // in-process ceiling and alarms; a reachable Redis reporting the slot held returns
    // false WITHOUT falling back, so the scan moves to the next slot.
    private bool TryAcquirePinned(string key, string owner, ref bool redisFaulted, out ILease backend)
    {
        if (_redis is not null && !redisFaulted)
        {
            try
            {
                if (_redis.TryAcquire(key, owner, _ttl))
                {
                    backend = _redis;
                    return true;
                }

                backend = _redis;
                return false;
            }
            catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
            {
                _logger.LogWarning("{SecurityEvent} capability={Capability}",
                    SecurityEvents.SharedStateRedisUnavailable, Capability);
                redisFaulted = true;
            }
        }

        backend = _fallback;
        return _fallback.TryAcquire(key, owner, _ttl);
    }
}
