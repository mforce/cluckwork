namespace Cluckwork.Infrastructure.RateLimiting;

using Cluckwork.Application.Common;
using Cluckwork.Infrastructure.SharedState;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

// #545 — a held report-concurrency permit: one lease slot, PINNED to the backend
// that granted it. Renews on a background loop while the report runs; releases
// (compare-and-delete) on DisposeAsync. Renewal is best-effort — a failed renew
// is logged, not fatal; the report still completes and a later Release that no
// longer holds the slot compare-fails harmlessly. Time comes only from the
// injected TimeProvider.
public sealed class ReportConcurrencyPermit : IAsyncDisposable
{
    private readonly ILease _lease;          // the PINNED backend
    private readonly string _key;
    private readonly string _owner;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly TimeSpan _ttl;
    private readonly TimeSpan _renewInterval;
    private readonly CancellationTokenSource _renewalCts;
    private readonly Task _renewalLoop;

    internal ReportConcurrencyPermit(
        ILease lease, string key, string owner,
        TimeProvider clock, ILogger logger,
        TimeSpan ttl, TimeSpan renewInterval)
    {
        _lease = lease;
        _key = key;
        _owner = owner;
        _clock = clock;
        _logger = logger;
        _ttl = ttl;
        _renewInterval = renewInterval;
        // Renewal lifetime == permit lifetime: it stops ONLY on DisposeAsync, never
        // on the acquiring request's token. If the client disconnects while the
        // server-side report keeps running, the slot must stay held until the work
        // actually finishes (== DisposeAsync) — tying renewal to RequestAborted would
        // stop renewing while the query runs and let the slot be stolen mid-report.
        _renewalCts = new CancellationTokenSource();
        _renewalLoop = RenewLoopAsync(_renewalCts.Token);
    }

    // The single renewal operation — the production loop AND the tests call this.
    // Best-effort: a Redis fault on the pinned backend is alarmed, not thrown.
    internal bool RenewOnce()
    {
        try
        {
            return _lease.Renew(_key, _owner, _ttl);
        }
        // Broad on purpose (unlike the acquire path): a fault here — a RedisException,
        // a timeout, or an ObjectDisposedException from a multiplexer torn down during
        // shutdown — must be a best-effort miss, never a throw. A throw would fault the
        // renewal loop and so bypass Release (leaking the slot until TTL). At acquire
        // time an unexpected fault fails CLOSED (a 500 denies capacity — safe); at
        // renew/release time it must fail SOFT, because a throw denies FUTURE capacity.
        catch (Exception)
        {
            _logger.LogWarning("{SecurityEvent} capability={Capability}",
                SecurityEvents.SharedStateRedisUnavailable,
                DistributedReportConcurrencyLimiter.Capability);
            return false;
        }
    }

    private async Task RenewLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(_renewInterval, _clock, token).ConfigureAwait(false);
                if (token.IsCancellationRequested)
                    break;
                RenewOnce();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal stop on cancellation (DisposeAsync cancelled the renewal CTS).
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _renewalCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _renewalLoop.ConfigureAwait(false);
        }
        // The renewal loop faulting for ANY reason must not stop us releasing the
        // slot — a leaked slot is held until TTL, the exact permit leak the
        // compare-and-delete release exists to prevent. DisposeAsync itself must
        // never throw: it runs in the endpoint filter's `await using` AFTER the
        // report result is produced, so a throw here would turn a successful
        // response into a 500 and waste the very work this cap protects.
        catch (Exception)
        {
        }

        // Compare-and-delete: only frees the slot if THIS owner still holds it.
        try
        {
            _lease.Release(_key, _owner);
        }
        catch (Exception)
        {
            _logger.LogWarning("{SecurityEvent} capability={Capability}",
                SecurityEvents.SharedStateRedisUnavailable,
                DistributedReportConcurrencyLimiter.Capability);
        }

        _renewalCts.Dispose();
    }
}
