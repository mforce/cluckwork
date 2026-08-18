namespace Cluckwork.Infrastructure.RateLimiting;

using Cluckwork.Application.Common;
using Cluckwork.Infrastructure.SharedState;
using Microsoft.Extensions.Logging;

// #545 — the outcome of a single renewal, which drives re-accounting.
// Renewed: the slot is still ours. Lost: a REACHABLE backend rejected the renewal
// (the slot's TTL lapsed and it may have been re-granted) — the running report is
// no longer counted. Faulted: the backend threw (alarmed) — the slot is probably
// still held, just unreachable, so this is NOT treated as a loss (re-acquiring
// would double-count).
internal enum RenewOutcome
{
    Renewed,
    Lost,
    Faulted,
}

// #545 — a held report-concurrency permit: one lease slot, PINNED to the backend
// that granted it. Renews on a background loop while the report runs; releases
// (compare-and-delete) on DisposeAsync. Time comes only from the injected
// TimeProvider.
//
// On a genuine loss (Lost) the tick re-grabs any free slot so the still-running
// report stays counted (owner decision: keep the report running, never cancel it).
// If no slot is free the account is genuinely over its ceiling with this report on
// top — a distinct alarm fires and the report continues (bounded, self-healing as
// reports finish).
public sealed class ReportConcurrencyPermit : IAsyncDisposable
{
    private readonly string _owner;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly TimeSpan _ttl;
    private readonly TimeSpan _renewInterval;
    private readonly Func<SlotGrant?> _reacquire;
    private readonly CancellationTokenSource _renewalCts;
    private readonly Task _renewalLoop;

    // Mutable: on a lease loss the permit adopts a freshly re-grabbed slot (a new
    // backend + key). Written only on the renewal-loop thread; read by Release in
    // DisposeAsync AFTER the loop is joined (CancelAsync + await), so the join is the
    // happens-before edge and no lock is needed.
    private ILease _lease;   // the PINNED backend
    private string _key;

    internal ReportConcurrencyPermit(
        ILease lease, string key, string owner,
        TimeProvider clock, ILogger logger,
        TimeSpan ttl, TimeSpan renewInterval,
        Func<SlotGrant?> reacquire)
    {
        _lease = lease;
        _key = key;
        _owner = owner;
        _clock = clock;
        _logger = logger;
        _ttl = ttl;
        _renewInterval = renewInterval;
        _reacquire = reacquire;
        // Renewal lifetime == permit lifetime: it stops ONLY on DisposeAsync, never
        // on the acquiring request's token. If the client disconnects while the
        // server-side report keeps running, the slot must stay held until the work
        // actually finishes (== DisposeAsync) — tying renewal to RequestAborted would
        // stop renewing while the query runs and let the slot be stolen mid-report.
        _renewalCts = new CancellationTokenSource();
        _renewalLoop = RenewLoopAsync(_renewalCts.Token);
    }

    // Renew the current slot. Renewed = still ours; Lost = a reachable backend
    // rejected us; Faulted = the backend threw (alarmed, NOT a loss).
    internal RenewOutcome RenewOnce()
    {
        try
        {
            return _lease.Renew(_key, _owner, _ttl) ? RenewOutcome.Renewed : RenewOutcome.Lost;
        }
        // Broad on purpose (unlike the acquire path): a fault here — a RedisException,
        // a timeout, or an ObjectDisposedException from a multiplexer torn down during
        // shutdown — must be a best-effort miss, never a throw. A throw would fault the
        // renewal loop and so bypass Release (leaking the slot until TTL).
        catch (Exception)
        {
            _logger.LogWarning("{SecurityEvent} capability={Capability}",
                SecurityEvents.SharedStateRedisUnavailable,
                DistributedReportConcurrencyLimiter.Capability);
            return RenewOutcome.Faulted;
        }
    }

    // One renewal tick — the production loop AND the tests call this. On a genuine
    // loss, re-grab any free slot so the running report stays counted; if none is
    // free, alarm over-capacity and keep running (never cancel).
    internal RenewOutcome RenewTick()
    {
        var outcome = RenewOnce();
        if (outcome == RenewOutcome.Lost)
            TryReacquireAfterLoss();
        return outcome;
    }

    private void TryReacquireAfterLoss()
    {
        var grant = _reacquire();
        if (grant is { } g)
        {
            // Adopt the re-grabbed slot; future renews/release target it.
            _lease = g.Backend;
            _key = g.Key;
            return;
        }

        // No free slot: the account is at its ceiling with this report on top.
        _logger.LogWarning("{SecurityEvent} capability={Capability}",
            SecurityEvents.ReportConcurrencyOverCapacity,
            DistributedReportConcurrencyLimiter.Capability);
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
                RenewTick();
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
