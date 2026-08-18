namespace Cluckwork.Api.IntegrationTests.SharedState;

using Cluckwork.Infrastructure.SharedState;

// #543 — Redis contract: fixed-window counter, against a REAL Redis
// (Testcontainers) with a REAL short window. The window is bucketed on
// Redis's OWN clock (TIME inside the script), so the rollover is observed
// with a real wait, not a fake clock.
public sealed class RedisFixedWindowCounterContractTests(RedisFixture fixture) : IClassFixture<RedisFixture>
{
    [Fact]
    public async Task IncrementsAccumulateThenResetAfterWindow()
    {
        var counter = new RedisFixedWindowCounter(fixture.Redis, Guid.NewGuid().ToString("N"));
        var window = TimeSpan.FromSeconds(2);

        // Align to just after the next window boundary so the two increments below
        // sit near the start of a fresh bucket (a localhost Testcontainers Redis
        // shares this host's clock, so client-side alignment matches the server
        // bucket). Without this, two calls straddling a boundary make the second
        // read 1 instead of 2 — an intermittent failure.
        var windowMs = (long)window.TotalMilliseconds;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var untilNextBoundary = windowMs - (nowMs % windowMs);
        await Task.Delay((int)untilNextBoundary + 50);

        Assert.Equal(1, counter.Increment("k", window));
        Assert.Equal(2, counter.Increment("k", window));

        // Wait past the window: the next increment starts a fresh bucket.
        await Task.Delay(window + TimeSpan.FromMilliseconds(300));
        Assert.Equal(1, counter.Increment("k", window));
    }

    [Fact]
    public async Task IncrementAsync_returns_count_and_positive_remaining()
    {
        var counter = new RedisFixedWindowCounter(fixture.Redis, Guid.NewGuid().ToString("N"));
        var window = TimeSpan.FromSeconds(30);

        var first = await counter.IncrementAsync("k", window);
        Assert.Equal(1, first.Count);
        Assert.True(first.Remaining > TimeSpan.Zero && first.Remaining <= window,
            $"remaining {first.Remaining} must be in (0, {window}]");

        var second = await counter.IncrementAsync("k", window);
        Assert.Equal(2, second.Count);
    }

    // #544 review (codex P2): the key must expire at the wall-clock BUCKET boundary, so the
    // returned Remaining (PTTL → async Retry-After) is the time left in the CURRENT window, not
    // a full window measured from the first request. Phase the first increment DEEP into a
    // bucket, then assert Remaining is well under a full window — a full-window TTL bug returns
    // ~the whole window here and fails. (The existing positive-remaining test uses a 30s window
    // and only asserts `<= window`, so a full-window TTL slips past it; this one catches it.)
    [Fact]
    public async Task IncrementAsync_remaining_is_time_to_bucket_boundary_not_a_full_window()
    {
        var counter = new RedisFixedWindowCounter(fixture.Redis, Guid.NewGuid().ToString("N"));
        var window = TimeSpan.FromMilliseconds(4000);

        // Wait until we are 50–62% into a 4s wall-clock bucket (a localhost Testcontainers Redis
        // shares this host's clock). A correct impl must then return well under a full window;
        // the buggy full-window TTL returns ~4s and stands out. The [2000,2500]ms band leaves
        // >1s of headroom before the boundary, so a slow increment cannot roll into a new bucket.
        while (true)
        {
            var phase = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 4000;
            if (phase is >= 2000 and <= 2500)
                break;
            await Task.Delay(40);
        }

        var result = await counter.IncrementAsync("boundary", window);

        Assert.Equal(1, result.Count);
        Assert.True(result.Remaining > TimeSpan.Zero,
            $"remaining {result.Remaining} must be positive");
        Assert.True(result.Remaining < TimeSpan.FromMilliseconds(3000),
            $"remaining {result.Remaining} must be time-to-boundary (< 3s), not a full 4s window");
    }

    [Fact]
    public void NonWholeMillisecondWindow_Throws()
    {
        var counter = new RedisFixedWindowCounter(fixture.Redis, Guid.NewGuid().ToString("N"));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => counter.Increment("k", TimeSpan.FromTicks(15000)));
    }
}
