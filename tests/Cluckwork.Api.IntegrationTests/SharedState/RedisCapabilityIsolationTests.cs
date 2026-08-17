namespace Cluckwork.Api.IntegrationTests.SharedState;

using Cluckwork.Infrastructure.SharedState;

// #543 — claim-once and lease must NOT collide when a caller uses the same
// logical key and namespace for both. Before the per-capability infix
// ("claim:" / "lease:"), both wrote "{ns}:{key}", so a live lease blocked a
// grant claim (and vice versa) — a cross-capability denial. Real Redis, since
// the collision is a Redis-keyspace property (the in-process impls use separate
// dictionaries and never collide).
public sealed class RedisCapabilityIsolationTests(RedisFixture fixture) : IClassFixture<RedisFixture>
{
    [Fact]
    public void ClaimOnceAndLease_WithSameKeyAndNamespace_DoNotCollide()
    {
        var ns = Guid.NewGuid().ToString("N");
        var claim = new RedisClaimOnceStore(fixture.Redis, ns);
        var lease = new RedisLease(fixture.Redis, ns);
        var ttl = TimeSpan.FromSeconds(5);

        // A live lease on "shared" must not stop a claim on "shared".
        Assert.True(lease.TryAcquire("shared", "owner-1", ttl));
        Assert.True(claim.TryClaim("shared", ttl));

        // And the claim must not have disturbed the lease: a second owner still
        // cannot take it, and the real owner can still release it.
        Assert.False(lease.TryAcquire("shared", "owner-2", ttl));
        Assert.True(lease.Release("shared", "owner-1"));
    }
}
