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
        for (var slot = 0; slot < _permitLimit; slot++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = $"report-cc:{accountId:N}:{slot}";
            if (TryAcquirePinned(key, owner, out var backend))
            {
                var permit = new ReportConcurrencyPermit(
                    backend, key, owner, _clock, _logger, _ttl, _renewInterval, cancellationToken);
                return new ValueTask<ReportConcurrencyPermit?>(permit);
            }
        }

        return new ValueTask<ReportConcurrencyPermit?>((ReportConcurrencyPermit?)null);
    }

    // Tries Redis first when configured; a Redis FAULT (not a held slot) alarms
    // and falls back to the in-process ceiling for THIS slot. A reachable Redis
    // that reports the slot held returns false WITHOUT falling back — the scan
    // moves to the next slot. `backend` is only meaningful when this returns true.
    private bool TryAcquirePinned(string key, string owner, out ILease backend)
    {
        if (_redis is not null)
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
            }
        }

        backend = _fallback;
        return _fallback.TryAcquire(key, owner, _ttl);
    }
}
