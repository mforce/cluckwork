namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.IntegrationTests.SharedState; // FakeTimeProvider (local test clock)
using Cluckwork.Infrastructure.RateLimiting;
using Cluckwork.Infrastructure.SharedState;
using System.Threading.RateLimiting;

// #544 — direct unit tests for the distributed per-IP limiter (no Docker: a
// hand-written counter stub + FakeTimeProvider). The integration wiring tests
// hit these paths only indirectly; this file covers the edges they don't:
//   - the ASYNC acquire path (the ASP.NET middleware hot path) takes its
//     Retry-After from the COUNTER's own remaining-window value, not a
//     host-clock recompute — the whole point of #544's IncrementAsync;
//   - permitCount == 0 is an availability probe that admits WITHOUT consuming
//     (the increment-only counter must never be called for it);
//   - permitCount > 1 throws (the shared port only increments by one).
public sealed class DistributedIpFixedWindowRateLimiterTests
{
    private const int PermitLimit = 3;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan FakeRemaining = TimeSpan.FromSeconds(42);

    // A caller-controlled counter: returns a preset Count and a fixed
    // Remaining from IncrementAsync (and the same Count from the sync
    // Increment). Records how often each overload was called, so the
    // probe/throw tests can assert the counter was (not) consumed.
    private sealed class FakeCounter : IFixedWindowCounter
    {
        public long NextCount { get; set; } = 1;
        public int SyncCalls { get; private set; }
        public int AsyncCalls { get; private set; }

        public long Increment(string key, TimeSpan window)
        {
            SyncCalls++;
            return NextCount;
        }

        public ValueTask<FixedWindowResult> IncrementAsync(
            string key, TimeSpan window, System.Threading.CancellationToken cancellationToken = default)
        {
            AsyncCalls++;
            return new ValueTask<FixedWindowResult>(new FixedWindowResult(NextCount, FakeRemaining));
        }
    }

    private static DistributedIpFixedWindowRateLimiter NewLimiter(FakeCounter counter) =>
        new(counter, new FakeTimeProvider(), "auth-login:203.0.113.1", permitLimit: PermitLimit, window: Window);

    [Fact]
    public async Task AcquireAsync_within_limit_is_acquired()
    {
        var counter = new FakeCounter();
        var limiter = NewLimiter(counter);

        for (var count = 1; count <= PermitLimit; count++)
        {
            counter.NextCount = count;
            var lease = await limiter.AcquireAsync(1);
            Assert.True(lease.IsAcquired, $"count {count} is within the limit of {PermitLimit}");
        }
        Assert.Equal(PermitLimit, counter.AsyncCalls);
    }

    [Fact]
    public async Task AcquireAsync_over_limit_is_rejected_with_counter_remaining_as_retry_after()
    {
        var counter = new FakeCounter { NextCount = PermitLimit + 1 };
        var limiter = NewLimiter(counter);

        var lease = await limiter.AcquireAsync(1);

        Assert.False(lease.IsAcquired);
        Assert.True(lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter),
            "a rejected lease must carry Retry-After metadata");
        // Proves the async path uses the COUNTER's remaining window, not a
        // host-clock recompute: the fake's 42s must come back verbatim.
        Assert.Equal(FakeRemaining, (TimeSpan)retryAfter!);
        Assert.Equal(0, counter.SyncCalls);
    }

    [Fact]
    public void AttemptAcquire_zero_permits_does_not_consume()
    {
        var counter = new FakeCounter { NextCount = PermitLimit + 1 };
        var limiter = NewLimiter(counter);

        var lease = limiter.AttemptAcquire(0);

        Assert.True(lease.IsAcquired);
        // The availability probe must not consume a permit on the shared counter.
        Assert.Equal(0, counter.SyncCalls);
        Assert.Equal(0, counter.AsyncCalls);
    }

    [Fact]
    public void AttemptAcquire_more_than_one_permit_throws()
    {
        var counter = new FakeCounter();
        var limiter = NewLimiter(counter);

        Assert.Throws<ArgumentOutOfRangeException>(() => limiter.AttemptAcquire(2));
        Assert.Equal(0, counter.SyncCalls);
    }
}
