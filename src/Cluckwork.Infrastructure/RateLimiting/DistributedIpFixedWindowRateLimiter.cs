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
        // Contract: the ASP.NET rate-limiting middleware acquires exactly one permit per
        // request. permitCount == 0 is the framework's "is anything available?" probe and
        // must NOT consume (the shared counter is increment-only, so we cannot peek — treat
        // the probe as always-available; the real request that follows does the increment).
        // Anything > 1 is unsupported: the shared port only increments by one.
        ArgumentOutOfRangeException.ThrowIfNegative(permitCount);
        if (permitCount == 0)
            return new DistributedLease(isAcquired: true, retryAfter: null);
        if (permitCount > 1)
            throw new ArgumentOutOfRangeException(nameof(permitCount),
                "DistributedIpFixedWindowRateLimiter supports at most one permit per acquisition.");

        Interlocked.Exchange(ref _lastActivityTimestamp, _timeProvider.GetTimestamp());

        // One increment per request, whether or not it is admitted: a rejected request
        // still counts (the counter is increment-only and cannot un-consume). Admit iff
        // the post-increment count is within the budget.
        var count = _counter.Increment(_key, _window);
        if (count <= _permitLimit)
            return new DistributedLease(isAcquired: true, retryAfter: null);

        return new DistributedLease(isAcquired: false, retryAfter: RetryAfterToWindowEnd());
    }

    protected override ValueTask<RateLimitLease> AcquireAsyncCore(
        int permitCount, CancellationToken cancellationToken)
    {
        // No queueing (QueueLimit is 0 for these policies) and the counter is synchronous,
        // so there is nothing to await — mirror the sync path exactly.
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<RateLimitLease>(AttemptAcquireCore(permitCount));
    }

    // Seconds until the current wall-clock-aligned window rolls over, computed the SAME way
    // the counter buckets (floor(epochMs / windowMs) * windowMs + windowMs). The global
    // OnRejected handler reads MetadataName.RetryAfter and ceils it to whole seconds.
    private TimeSpan RetryAfterToWindowEnd()
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
