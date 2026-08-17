namespace Cluckwork.Api.IntegrationTests.SharedState;

using Cluckwork.Infrastructure.SharedState;

// #543 — contract: owned, renewable lease with compare-and-delete release.
//
// Implementation-under-test: the in-process fallback via a single swappable
// factory. A later increment re-points this suite at Redis by changing
// <see cref="CreateLease"/> only — the assertions are the contract and stay
// byte-identical.
public sealed class LeaseContractTests
{
    private static ILease CreateLease(TimeProvider timeProvider) =>
        new InProcessLease(timeProvider);

    private sealed class Fixture
    {
        public Fixture()
        {
            Clock = new FakeTimeProvider();
            Lease = CreateLease(Clock);
        }

        public FakeTimeProvider Clock { get; }
        public ILease Lease { get; }
    }

    [Fact]
    public void Acquire_WhenFree_Succeeds()
    {
        var fixture = new Fixture();

        Assert.True(fixture.Lease.TryAcquire("k", "owner-1", TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Acquire_BySecondOwner_WhileHeld_Fails()
    {
        var fixture = new Fixture();

        Assert.True(fixture.Lease.TryAcquire("k", "owner-1", TimeSpan.FromMinutes(5)));

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(fixture.Lease.TryAcquire("k", "owner-2", TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Renew_ByOwner_SucceedsAndExtendsTtl()
    {
        var fixture = new Fixture();

        Assert.True(fixture.Lease.TryAcquire("k", "owner-1", TimeSpan.FromMinutes(5)));

        // Renew near the original expiry: the new TTL is measured from now.
        fixture.Clock.Advance(TimeSpan.FromMinutes(4));
        Assert.True(fixture.Lease.Renew("k", "owner-1", TimeSpan.FromMinutes(5)));

        // 4 min from the RENEWAL (8 min from acquisition) is still within
        // the renewed TTL — the original owner is still in.
        fixture.Clock.Advance(TimeSpan.FromMinutes(4));
        Assert.True(fixture.Lease.Renew("k", "owner-1", TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Renew_ByNonOwner_Fails()
    {
        var fixture = new Fixture();

        Assert.True(fixture.Lease.TryAcquire("k", "owner-1", TimeSpan.FromMinutes(5)));

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(fixture.Lease.Renew("k", "owner-2", TimeSpan.FromMinutes(5)));

        // The original holder is unaffected by the failed renewal.
        Assert.True(fixture.Lease.Renew("k", "owner-1", TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Release_ByOwner_Succeeds()
    {
        var fixture = new Fixture();

        Assert.True(fixture.Lease.TryAcquire("k", "owner-1", TimeSpan.FromMinutes(5)));

        Assert.True(fixture.Lease.Release("k", "owner-1"));
    }

    [Fact]
    public void Release_ByNonOwner_Fails()
    {
        var fixture = new Fixture();

        Assert.True(fixture.Lease.TryAcquire("k", "owner-1", TimeSpan.FromMinutes(5)));

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(fixture.Lease.Release("k", "owner-2"));

        // The original holder can still release.
        Assert.True(fixture.Lease.Release("k", "owner-1"));
    }

    [Fact]
    public void AfterExpiry_NewOwnerAcquires_OldOwnerReleaseFails()
    {
        var fixture = new Fixture();

        Assert.True(fixture.Lease.TryAcquire("k", "owner-1", TimeSpan.FromMinutes(5)));

        // TTL elapses; the lease is free again.
        fixture.Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        Assert.True(fixture.Lease.TryAcquire("k", "owner-2", TimeSpan.FromMinutes(5)));

        // Compare-and-delete: the previous owner must NOT release the lease
        // that was re-granted to someone else.
        Assert.False(fixture.Lease.Release("k", "owner-1"));

        // …and the new holder is still in.
        Assert.False(fixture.Lease.TryAcquire("k", "owner-3", TimeSpan.FromMinutes(5)));
        Assert.True(fixture.Lease.Release("k", "owner-2"));
    }

    [Fact]
    public void Acquire_AtExactExpiryInstant_ByNewOwner_Succeeds()
    {
        // now == entry.Expires means the lease has ELAPSED: it is free. A
        // `<`->`<=` mutation on the liveness check would wrongly hold it.
        var fixture = new Fixture();

        Assert.True(fixture.Lease.TryAcquire("k", "owner-1", TimeSpan.FromMinutes(5)));
        fixture.Clock.Advance(TimeSpan.FromMinutes(5)); // exactly to expiry

        Assert.True(fixture.Lease.TryAcquire("k", "owner-2", TimeSpan.FromMinutes(5)));
    }
}
