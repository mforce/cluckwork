namespace Cluckwork.Api.IntegrationTests.SharedState;

using Cluckwork.Infrastructure.SharedState;

// #543 — contract: single-use claim with TTL.
//
// Implementation-under-test: the in-process fallback via a single swappable
// factory. A later increment re-points this suite at Redis by changing
// <see cref="CreateStore"/> only — the assertions are the contract and stay
// byte-identical.
public sealed class ClaimOnceContractTests
{
    private static IClaimOnceStore CreateStore(TimeProvider timeProvider) =>
        new InProcessClaimOnceStore(timeProvider);

    private sealed class Fixture
    {
        public Fixture()
        {
            Clock = new FakeTimeProvider();
            Store = CreateStore(Clock);
        }

        public FakeTimeProvider Clock { get; }
        public IClaimOnceStore Store { get; }
    }

    [Fact]
    public void FirstClaim_Succeeds()
    {
        var fixture = new Fixture();

        Assert.True(fixture.Store.TryClaim("k", TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void SecondClaim_BeforeTtl_Fails()
    {
        var fixture = new Fixture();

        Assert.True(fixture.Store.TryClaim("k", TimeSpan.FromMinutes(5)));
        fixture.Clock.Advance(TimeSpan.FromMinutes(4));

        Assert.False(fixture.Store.TryClaim("k", TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Claim_AfterTtlElapses_SucceedsAgain()
    {
        var fixture = new Fixture();

        Assert.True(fixture.Store.TryClaim("k", TimeSpan.FromMinutes(5)));
        fixture.Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        Assert.True(fixture.Store.TryClaim("k", TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void DifferentKeys_AreIndependent()
    {
        var fixture = new Fixture();

        Assert.True(fixture.Store.TryClaim("a", TimeSpan.FromMinutes(5)));
        Assert.True(fixture.Store.TryClaim("b", TimeSpan.FromMinutes(5)));

        // Claiming "a" again is refused while its TTL is live, even though
        // "b" was claimed in between.
        Assert.False(fixture.Store.TryClaim("a", TimeSpan.FromMinutes(5)));
    }
}
