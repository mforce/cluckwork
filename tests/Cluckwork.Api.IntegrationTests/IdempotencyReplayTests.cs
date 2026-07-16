namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

// tech spec §11 / functional §23: replaying a write with the same Idempotency-Key must
// return the original response and never duplicate the side effect.
[Collection(IntegrationCollection.Name)]
public sealed class IdempotencyReplayTests(CluckworkWebApplicationFactory factory)
{
    private static object DailyEntryBody(Guid farmId, Guid houseId, Guid flockId) => new
    {
        farmId,
        houseId,
        flockId,
        date = DateOnly.FromDateTime(DateTime.UtcNow.Date),
        totalEggs = 200,
        crackedEggs = 3,
        dirtyEggs = 2,
        discardedEggs = 1,
        mortalityCount = 0
    };

    [Fact]
    public async Task SameKey_ReplaysResponse_AndCreatesRowOnce()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        var farmId = Guid.NewGuid();
        var houseId = Guid.NewGuid();
        var flockId = await factory.SeedFlockAsync(accountId, farmId, houseId);
        var body = DailyEntryBody(farmId, houseId, flockId);
        var key = Guid.NewGuid().ToString();

        var first = await client.PostWithKeyAsync("/api/v1/daily-entries", key, body);
        var second = await client.PostWithKeyAsync("/api/v1/daily-entries", key, body);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());

        // Exactly one row persisted for the natural key despite two POSTs.
        var count = await factory.WithTenantScopeAsync(accountId, db =>
            db.DailyEntries.CountAsync(e =>
                e.FarmId == farmId && e.HouseId == houseId && e.FlockId == flockId));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task MissingKey_Returns400()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        var response = await client.PostAsJsonAsync("/api/v1/daily-entries",
            DailyEntryBody(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
