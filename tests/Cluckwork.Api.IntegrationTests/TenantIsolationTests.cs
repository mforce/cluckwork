namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Eggs;
using Microsoft.EntityFrameworkCore;

// tech spec §4.2 / §11: a token for account A can never read or write account B's data.
[Collection(IntegrationCollection.Name)]
public sealed class TenantIsolationTests(CluckworkWebApplicationFactory factory)
{
    [Fact]
    public async Task QueryFilter_HidesOtherAccountsRows()
    {
        var accountA = await factory.SeedAccountWithUserAsync($"a-{Guid.NewGuid():N}@test.local");
        var accountB = await factory.SeedAccountWithUserAsync($"b-{Guid.NewGuid():N}@test.local");

        // Seed a daily entry owned by account A.
        var farmId = Guid.NewGuid();
        var houseId = Guid.NewGuid();
        var flockId = Guid.NewGuid();
        await factory.WithTenantScopeAsync(accountA, async db =>
        {
            var entry = DailyEntry.Create(Guid.NewGuid(), accountA, farmId, houseId, flockId,
                DateOnly.FromDateTime(DateTime.UtcNow.Date));
            entry.RecordProduction(100, 1, 1, 0, 0);
            db.DailyEntries.Add(entry);
            await db.SaveChangesAsync();
        });

        // Account B sees none of A's rows through the global query filter.
        var visibleToB = await factory.WithTenantScopeAsync(accountB, db =>
            db.DailyEntries.CountAsync());
        Assert.Equal(0, visibleToB);

        // Account A sees exactly its own row.
        var visibleToA = await factory.WithTenantScopeAsync(accountA, db =>
            db.DailyEntries.CountAsync());
        Assert.Equal(1, visibleToA);
    }

    [Fact]
    public async Task ConfirmSale_ForOtherAccountsOrder_Returns404()
    {
        var accountA = await factory.SeedAccountWithUserAsync($"a-{Guid.NewGuid():N}@test.local");
        var userB = $"b-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(userB);

        // Order + stock belong to account A.
        await factory.SeedEggLotAsync(accountA, "A-Large", 100);
        var orderId = await factory.SeedSalesOrderAsync(accountA, "A-Large", 10);

        // Account B, fully authenticated, tries to confirm A's order.
        var tokenB = await factory.LoginForAccessTokenAsync(userB);
        var clientB = factory.CreateAuthedClient(tokenB);
        var response = await clientB.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString());

        // Tenant mismatch is masked as NotFound so existence isn't leaked.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // A's order remains draft; no allocation happened.
        var stillDraft = await factory.WithTenantScopeAsync(accountA, async db =>
        {
            var lot = await db.EggLots.FirstAsync();
            return lot.QuantityAvailable;
        });
        Assert.Equal(100, stillDraft);
    }
}
