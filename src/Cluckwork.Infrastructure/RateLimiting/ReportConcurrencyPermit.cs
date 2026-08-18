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
        TimeSpan ttl, TimeSpan renewInterval,
        CancellationToken acquireToken)
    {
        _lease = lease;
        _key = key;
        _owner = owner;
        _clock = clock;
        _logger = logger;
        _ttl = ttl;
        _renewInterval = renewInterval;
        // Renewal stops when the acquiring request ends (acquireToken) OR on Dispose.
        _renewalCts = CancellationTokenSource.CreateLinkedTokenSource(acquireToken);
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
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
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
            // Normal stop on cancellation (acquire token ended or DisposeAsync).
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _renewalCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _renewalLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        // Compare-and-delete: only frees the slot if THIS owner still holds it.
        try
        {
            _lease.Release(_key, _owner);
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            _logger.LogWarning("{SecurityEvent} capability={Capability}",
                SecurityEvents.SharedStateRedisUnavailable,
                DistributedReportConcurrencyLimiter.Capability);
        }

        _renewalCts.Dispose();
    }
}
