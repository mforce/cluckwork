namespace Cluckwork.Api.IntegrationTests.SharedState;

using Cluckwork.Infrastructure.SharedState;

// #543 — contract: atomic increment within a fixed time window.
//
// Implementation-under-test: the in-process fallback via a single swappable
// factory. A later increment re-points this suite at Redis by changing
// <see cref="CreateCounter"/> only — the assertions are the contract and stay
// byte-identical.
public sealed class FixedWindowCounterContractTests
{
    private static IFixedWindowCounter CreateCounter(TimeProvider timeProvider) =>
        new InProcessFixedWindowCounter(timeProvider);

    [Fact]
    public void Increments_AccumulateWithinWindow()
    {
        var clock = new FakeTimeProvider();
        var counter = CreateCounter(clock);

        Assert.Equal(1, counter.Increment("k", TimeSpan.FromMinutes(5)));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(2, counter.Increment("k", TimeSpan.FromMinutes(5)));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(3, counter.Increment("k", TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Count_ResetsWhenWindowRollsOver()
    {
        var clock = new FakeTimeProvider();
        var counter = CreateCounter(clock);

        Assert.Equal(1, counter.Increment("k", TimeSpan.FromMinutes(5)));
        Assert.Equal(2, counter.Increment("k", TimeSpan.FromMinutes(5)));

        // Advance just past the window boundary: the count starts fresh.
        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        Assert.Equal(1, counter.Increment("k", TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void DifferentKeys_AreIndependent()
    {
        var clock = new FakeTimeProvider();
        var counter = CreateCounter(clock);

        // Separate counts for separate keys within the same window.
        Assert.Equal(1, counter.Increment("a", TimeSpan.FromMinutes(5)));
        Assert.Equal(2, counter.Increment("a", TimeSpan.FromMinutes(5)));
        Assert.Equal(1, counter.Increment("b", TimeSpan.FromMinutes(5)));

        // The window is wall-clock-aligned: past the boundary EVERY key resets
        // to its own fresh count — nothing carries over between keys.
        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        Assert.Equal(1, counter.Increment("a", TimeSpan.FromMinutes(5)));
        Assert.Equal(1, counter.Increment("b", TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void SubSecondWindow_DoesNotCrashAndBucketsOnMilliseconds()
    {
        // Regression: (long)window.TotalSeconds was 0 for any window < 1s, so
        // the modulo in WindowStart threw DivideByZeroException. It must now
        // behave like any other window.
        var clock = new FakeTimeProvider();
        var counter = CreateCounter(clock);
        var window = TimeSpan.FromMilliseconds(500);

        Assert.Equal(1, counter.Increment("k", window));
        Assert.Equal(2, counter.Increment("k", window));

        // Past the 500ms window the count resets.
        clock.Advance(window + TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, counter.Increment("k", window));
    }

    [Fact]
    public void WindowBelowOneMillisecond_Throws()
    {
        var counter = CreateCounter(new FakeTimeProvider());

        Assert.Throws<ArgumentOutOfRangeException>(
            () => counter.Increment("k", TimeSpan.FromMicroseconds(500)));
    }

    [Fact]
    public void Sweep_DoesNotEvictLiveLongerWindowCounter()
    {
        // Regression (codex): the sweep compared every entry's WindowStart to
        // the current caller's windowStart, so a 5-minute caller's sweep evicted
        // a still-live long-window counter. It must drop on each entry's OWN
        // expiry. The sweep fires only when the table exceeds 4096 entries and
        // the internal counter hits a multiple of 256 — 1 + 4096 + 255 = 4352.
        var clock = new FakeTimeProvider();
        var counter = CreateCounter(clock);
        var longWindow = TimeSpan.FromHours(1);
        var shortWindow = TimeSpan.FromMinutes(5);

        // A live long-window counter at t0.
        Assert.Equal(1, counter.Increment("long", longWindow));

        // 4096 short-window keys at t0 -> table now exceeds the 4096 threshold.
        for (var i = 0; i < 4096; i++)
            counter.Increment($"short-{i}", shortWindow);

        // Past the short window, well within the long one: every short entry is
        // now expired; the long entry is still live.
        clock.Advance(TimeSpan.FromMinutes(6));

        // Drive the internal sweep counter to the next multiple of 256 (4352)
        // so a sweep runs. Re-incrementing ONE key keeps the table size.
        for (var i = 0; i < 255; i++)
            counter.Increment("short-0", shortWindow);

        // The long-window counter survived the sweep: its count continues from
        // 1 -> 2 rather than restarting at 1.
        Assert.Equal(2, counter.Increment("long", longWindow));
    }
}
