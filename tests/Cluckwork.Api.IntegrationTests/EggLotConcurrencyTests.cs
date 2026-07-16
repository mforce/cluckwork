namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

// tech spec §3.3 / §10.9.1 / §11: concurrent confirmation against the same egg lot must
// serialize on the FOR UPDATE lock — exactly one sale allocates, the other is rejected,
// and the lot is never oversold.
[Collection(IntegrationCollection.Name)]
public sealed class EggLotConcurrencyTests(CluckworkWebApplicationFactory factory)
{
    [Fact]
    public async Task TwoOrdersRacingForOneLot_OneSucceeds_OneRejected_NoOversell()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);

        var grades = await factory.SeedEggGradesAsync(accountId, Guid.NewGuid(), "Large");

        // One lot of 100; two orders each wanting the whole 100.
        await factory.SeedEggLotAsync(accountId, grades["Large"], 100);
        var orderA = await factory.SeedSalesOrderAsync(accountId, grades["Large"], 100);
        var orderB = await factory.SeedSalesOrderAsync(accountId, grades["Large"], 100);

        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        var confirmA = client.PostWithKeyAsync($"/api/v1/sales/{orderA}/confirm", Guid.NewGuid().ToString());
        var confirmB = client.PostWithKeyAsync($"/api/v1/sales/{orderB}/confirm", Guid.NewGuid().ToString());
        var responses = await Task.WhenAll(confirmA, confirmB);

        var ok = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        var rejected = responses.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity);

        Assert.Equal(1, ok);
        Assert.Equal(1, rejected);

        // Lot fully consumed exactly once — never negative, never partially double-spent.
        var remaining = await factory.WithTenantScopeAsync(accountId, async db =>
            (await db.EggLots.FirstAsync()).QuantityAvailable);
        Assert.Equal(0, remaining);
    }
}
