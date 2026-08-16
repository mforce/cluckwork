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
}
