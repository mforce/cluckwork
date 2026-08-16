namespace Cluckwork.Api.IntegrationTests.SharedState;

using Cluckwork.Infrastructure.SharedState;

// #543 — Redis contract: owned, renewable lease with compare-and-delete
// release, against a REAL Redis (Testcontainers) with REAL short TTLs.
public sealed class RedisLeaseContractTests(RedisFixture fixture) : IClassFixture<RedisFixture>
{
    [Fact]
    public async Task Acquire_Renew_Release_Expiry()
    {
        var lease = new RedisLease(fixture.Redis, Guid.NewGuid().ToString("N"));
        var ttl = TimeSpan.FromSeconds(2);

        // Free lease: the first owner acquires.
        Assert.True(lease.TryAcquire("k", "owner-1", ttl));

        // Held: a second owner cannot acquire while the lease is live.
        Assert.False(lease.TryAcquire("k", "owner-2", ttl));

        // Renew: the owner extends; a non-owner is refused.
        Assert.True(lease.Renew("k", "owner-1", ttl));
        Assert.False(lease.Renew("k", "owner-2", ttl));

        // Release: a non-owner is refused; the owner releases.
        Assert.False(lease.Release("k", "owner-2"));
        Assert.True(lease.Release("k", "owner-1"));

        // Free again: a different owner can acquire.
        Assert.True(lease.TryAcquire("k", "owner-2", ttl));

        // Past the TTL the lease is free: a third owner can acquire.
        await Task.Delay(ttl + TimeSpan.FromMilliseconds(200));
        Assert.True(lease.TryAcquire("k", "owner-3", ttl));
    }
}
