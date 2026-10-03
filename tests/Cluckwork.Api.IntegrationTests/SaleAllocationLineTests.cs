using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Api.IntegrationTests;

// #854: each allocation row names the order line it was drawn for. The admin
// export and the sale-to-lot chain (spec §9.6) read SalesOrderItemId, and only
// a multi-line order can show a row stamped with the wrong line.
[Collection(IntegrationCollection.Name)]
public sealed class SaleAllocationLineTests(CluckworkWebApplicationFactory factory)
{
    [Fact]
    public async Task Confirm_MultiLineOrder_EachAllocationNamesTheLineItWasDrawnFor()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var grades = await factory.SeedEggGradesAsync(accountId, Guid.NewGuid(), "Large", "Medium");
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        await factory.SeedEggLotAsync(accountId, grades["Large"], 30, productionDate: today);
        await factory.SeedEggLotAsync(accountId, grades["Medium"], 30, productionDate: today);
        var order = await factory.SeedSalesOrderAsync(accountId, [(grades["Large"], 10), (grades["Medium"], 4)]);

        (await client.PostWithKeyAsync($"/api/v1/sales/{order}/confirm", Guid.NewGuid().ToString()))
            .EnsureSuccessStatusCode();

        var rows = await factory.WithTenantScopeAsync(accountId, db =>
            (from allocation in db.SalesOrderAllocations
             join line in db.SalesOrderItems on allocation.SalesOrderItemId equals line.Id
             join lot in db.EggLots on allocation.EggLotId equals lot.Id
             where allocation.SalesOrderId == order
             orderby allocation.Quantity
             select new { LineGrade = line.EggGradeId, LotGrade = lot.EggGradeId, allocation.Quantity })
            .ToListAsync());
        Assert.Equal(
            [(grades["Medium"], grades["Medium"], 4), (grades["Large"], grades["Large"], 10)],
            rows.Select(r => (r.LineGrade, r.LotGrade, r.Quantity)));
    }
}
