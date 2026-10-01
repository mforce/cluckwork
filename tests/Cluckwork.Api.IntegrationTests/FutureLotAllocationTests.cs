namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;

// #35: the stock read and FIFO allocation must agree about what exists. The
// stock query has always hidden future-dated production ("if it ever slips in");
// the allocation query did not, so a lot dated ahead of today was invisible in
// stock yet sellable. Such lots can be in real data already — the +1-day
// validator slack removed in #35 is exactly what let an entry be dated tomorrow.
[Collection(IntegrationCollection.Name)]
public sealed class FutureLotAllocationTests(CluckworkWebApplicationFactory factory)
{
    [Fact]
    public async Task FutureDatedLot_IsNeitherCountedInStock_NorAllocatable()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var grades = await factory.SeedEggGradesAsync(accountId, Guid.NewGuid(), "Large");

        // The only stock is dated tomorrow.
        var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow.Date).AddDays(1);
        await factory.SeedEggLotAsync(
            accountId, grades["Large"], 100, restrictedUntil: null, productionDate: tomorrow);
        var order = await factory.SeedSalesOrderAsync(accountId, grades["Large"], 100);

        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        // Stock hides it...
        var stock = await client.GetFromJsonAsync<List<StockRow>>("/api/v1/stock");
        var large = stock!.SingleOrDefault(r => r.EggGradeId == grades["Large"]);
        Assert.True(large is null || large.Available == 0,
            "a lot produced tomorrow must not count as stock today");

        // ...so the sale must not be able to draw on it either.
        var confirm = await client.PostWithKeyAsync(
            $"/api/v1/sales/{order}/confirm", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, confirm.StatusCode);
    }

    private sealed record StockRow(Guid EggGradeId, string GradeName, int Available, int Restricted);
}
