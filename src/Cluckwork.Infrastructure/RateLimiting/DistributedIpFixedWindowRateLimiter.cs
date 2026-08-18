namespace Cluckwork.Infrastructure.RateLimiting;

using System.Threading;
using System.Threading.RateLimiting;
using Cluckwork.Infrastructure.SharedState;

// #544 — the per-IP fixed-window RateLimiter that enforces its budget through the
// SHARED IFixedWindowCounter (Redis-backed with in-process fallback, #543) instead of
// ASP.NET's per-process partition state. One instance is created per partition key (the
// derived client-IP key) by DistributedIpFixedWindowPolicy and cached by the framework's
// PartitionedRateLimiter; the WINDOW itself is owned entirely by the counter (half-open
// [floor(now/w), floor(now/w)+w), count resets on rollover), so this type keeps no window
// state of its own — re-creating it after an idle eviction cannot reset a live budget,
// because the count lives in the shared store keyed by the same IP.
internal sealed class DistributedIpFixedWindowRateLimiter : RateLimiter
{
    private readonly IFixedWindowCounter _counter;
    private readonly TimeProvider _timeProvider;
    private readonly string _key;
    private readonly int _permitLimit;
    private readonly TimeSpan _window;
    private long _lastActivityTimestamp;

    public DistributedIpFixedWindowRateLimiter(
        IFixedWindowCounter counter,
        TimeProvider timeProvider,
        string key,
        int permitLimit,
        TimeSpan window)
    {
        _counter = counter;
        _timeProvider = timeProvider;
        _key = key;
        _permitLimit = permitLimit;
        _window = window;
        _lastActivityTimestamp = timeProvider.GetTimestamp();
    }

    // Time since the last acquire, so the framework's partition manager can evict this
    // idle per-IP limiter object (bounding object footprint under many distinct IPs).
    // Safe to evict: the shared counter, not this object, holds the window count.
    public override TimeSpan? IdleDuration =>
        _timeProvider.GetElapsedTime(Interlocked.Read(ref _lastActivityTimestamp));

    // No cheap cross-store snapshot — statistics are best-effort and unavailable here.
    public override RateLimiterStatistics? GetStatistics() => null;

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        var permitted = TryConsumePermit(permitCount, out var earlyLease);
        if (!permitted)
            return earlyLease!;

        // Synchronous path: NOT the ASP.NET rate-limiting middleware hot path (that awaits
        // AcquireAsync -> AcquireAsyncCore below). Kept for the RateLimiter contract and any
        // direct AttemptAcquire caller. Retry-After is a host-clock estimate here because the
        // synchronous counter returns only the count; the async path uses the counter's own clock.
        var count = _counter.Increment(_key, _window);
        return count <= _permitLimit
            ? new DistributedLease(isAcquired: true, retryAfter: null)
            : new DistributedLease(isAcquired: false, retryAfter: HostClockRetryAfter());
    }

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(
        int permitCount, CancellationToken cancellationToken)
    {
        var permitted = TryConsumePermit(permitCount, out var earlyLease);
        if (!permitted)
            return earlyLease!;

        // Async on purpose: the shared counter's Redis round trip must NOT block the ASP.NET
        // request thread on the auth hot path (#544 review). The count and the remaining window
        // both come from the counter (Redis server clock), so the Retry-After never drifts
        // against the API host clock.
        var result = await _counter.IncrementAsync(_key, _window, cancellationToken);
        return result.Count <= _permitLimit
            ? new DistributedLease(isAcquired: true, retryAfter: null)
            : new DistributedLease(isAcquired: false, retryAfter: result.Remaining);
    }

    // Shared guard for both acquire paths. Returns false with an early lease when the caller's
    // permitCount is the framework's availability probe (0 -> admit, no consume) or unsupported
    // (> 1); returns true to proceed with exactly one increment. Also records activity for
    // IdleDuration-based eviction.
    private bool TryConsumePermit(int permitCount, out RateLimitLease? earlyLease)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(permitCount);
        if (permitCount == 0)
        {
            // Availability probe: the increment-only counter cannot peek, so treat it as
            // available without consuming; the real request that follows does the increment.
            earlyLease = new DistributedLease(isAcquired: true, retryAfter: null);
            return false;
        }
        if (permitCount > 1)
            throw new ArgumentOutOfRangeException(nameof(permitCount),
                "DistributedIpFixedWindowRateLimiter supports at most one permit per acquisition.");

        Interlocked.Exchange(ref _lastActivityTimestamp, _timeProvider.GetTimestamp());
        earlyLease = null;
        return true;
    }

    // Seconds until the current wall-clock-aligned window rolls over, computed the SAME way the
    // in-process counter buckets. Used ONLY by the synchronous path (see AttemptAcquireCore); the
    // async path takes the remaining window from the counter itself.
    private TimeSpan HostClockRetryAfter()
    {
        var windowMs = (long)_window.TotalMilliseconds;
        var epochMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var windowStartMs = epochMs >= 0 || epochMs % windowMs == 0
            ? epochMs / windowMs * windowMs
            : (epochMs / windowMs - 1) * windowMs;
        var remainingMs = windowStartMs + windowMs - epochMs;
        return TimeSpan.FromMilliseconds(remainingMs);
    }

    private sealed class DistributedLease : RateLimitLease
    {
        private readonly TimeSpan? _retryAfter;

        public DistributedLease(bool isAcquired, TimeSpan? retryAfter)
        {
            IsAcquired = isAcquired;
            _retryAfter = retryAfter;
        }

        public override bool IsAcquired { get; }

        public override IEnumerable<string> MetadataNames =>
            _retryAfter is null ? [] : [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (_retryAfter is { } retryAfter && metadataName == MetadataName.RetryAfter.Name)
            {
                metadata = retryAfter;
                return true;
            }

            metadata = null;
            return false;
        }
    }
}
