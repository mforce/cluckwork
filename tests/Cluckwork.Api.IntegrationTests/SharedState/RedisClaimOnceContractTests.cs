namespace Cluckwork.Api.IntegrationTests.SharedState;

using Cluckwork.Infrastructure.SharedState;

// #543 — Redis contract: single-use claim with TTL, against a REAL Redis
// (Testcontainers) with REAL short TTLs. The impls honour Redis's server
// clock, so expiry is observed with a real wait, not a fake clock.
public sealed class RedisClaimOnceContractTests(RedisFixture fixture) : IClassFixture<RedisFixture>
{
    [Fact]
    public async Task FirstClaimTrue_SecondFalse_AfterTtlTrueAgain()
    {
        var store = new RedisClaimOnceStore(fixture.Redis, Guid.NewGuid().ToString("N"));
        var ttl = TimeSpan.FromSeconds(1);

        Assert.True(store.TryClaim("k", ttl));

        // While the claim is live, no second claim can land.
        Assert.False(store.TryClaim("k", ttl));

        // Past the TTL, the key is claimable again.
        await Task.Delay(ttl + TimeSpan.FromMilliseconds(200));
        Assert.True(store.TryClaim("k", ttl));
    }
}
