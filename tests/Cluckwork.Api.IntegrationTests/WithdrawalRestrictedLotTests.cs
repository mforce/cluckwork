namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

// tech spec §11 / functional §10.10, §13.3: a lot under medication withdrawal must be
// hard-blocked from sale. With no other stock, confirmation fails and the lot is untouched.
[Collection(IntegrationCollection.Name)]
public sealed class WithdrawalRestrictedLotTests(CluckworkWebApplicationFactory factory)
{
    [Fact]
    public async Task RestrictedLot_CannotBeSold()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);

        var grades = await factory.SeedEggGradesAsync(accountId, Guid.NewGuid(), "Large");

        // The only stock for this grade is restricted for another week.
        var restrictedUntil = DateOnly.FromDateTime(DateTime.UtcNow.Date).AddDays(7);
        await factory.SeedEggLotAsync(accountId, grades["Large"], 100, restrictedUntil);
        var orderId = await factory.SeedSalesOrderAsync(accountId, grades["Large"], 10);

        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        var response = await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString());

        // No available (unrestricted) stock → business-rule rejection, not a crash.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        // The restricted lot is left completely untouched.
        var remaining = await factory.WithTenantScopeAsync(accountId, async db =>
            (await db.EggLots.FirstAsync()).QuantityAvailable);
        Assert.Equal(100, remaining);
    }
}
