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
}
