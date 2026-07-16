namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;

// #7 / #9 / #12 — the read tier: daily-entry get/list, stock by grade, sales
// order get/list. All tenant-scoped via the global query filter.
[Collection(IntegrationCollection.Name)]
public sealed class ReadEndpointTests(CluckworkWebApplicationFactory factory)
{
    private sealed record IdDto(Guid Id);
    private sealed record GradeLineDto(Guid EggGradeId, int Quantity);
    private sealed record EntryDto(
        Guid Id, Guid FlockId, DateOnly Date, string Status, int TotalEggs, List<GradeLineDto> Grades);
    private sealed record StockDto(Guid EggGradeId, string GradeName, int Available, int Restricted);
    private sealed record OrderItemDto(Guid EggGradeId, int Quantity);
    private sealed record OrderDto(Guid Id, string Status, List<OrderItemDto> Items);

    private async Task<(HttpClient Client, Guid AccountId, Guid FarmId, Dictionary<string, Guid> Grades)>
        SetupAsync(params string[] gradeNames)
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var farmId = Guid.NewGuid();
        var grades = await factory.SeedEggGradesAsync(accountId, farmId, gradeNames);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        return (client, accountId, farmId, grades);
    }

    private static object EntryBody(Guid farmId, Guid flockId, DateOnly date, object[] grades) => new
    {
        farmId,
        houseId = Guid.NewGuid(),
        flockId,
        date,
        totalEggs = 1000,
        crackedEggs = 10,
        dirtyEggs = 5,
        discardedEggs = 3,
        mortalityCount = 0,
        grades
    };

    [Fact]
    public async Task GetDailyEntry_ReturnsGradeLines()
    {
        var (client, _, farmId, grades) = await SetupAsync("Large");
        var flockId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

        var create = await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(),
            EntryBody(farmId, flockId, today, [new { eggGradeId = grades["Large"], quantity = 600 }]));
        var id = (await create.Content.ReadFromJsonAsync<IdDto>())!.Id;

        var entry = await client.GetFromJsonAsync<EntryDto>($"/api/v1/daily-entries/{id}");

        Assert.Equal(flockId, entry!.FlockId);
        Assert.Equal("Draft", entry.Status);
        Assert.Equal(1000, entry.TotalEggs);
        var line = Assert.Single(entry.Grades);
        Assert.Equal(grades["Large"], line.EggGradeId);
        Assert.Equal(600, line.Quantity);
    }

    [Fact]
    public async Task ListDailyEntries_FiltersByFlock_NewestFirst()
    {
        var (client, _, farmId, grades) = await SetupAsync("Large");
        var flockA = Guid.NewGuid();
        var flockB = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

        foreach (var (flock, date) in new[] { (flockA, today.AddDays(-2)), (flockA, today), (flockB, today) })
            await client.PostWithKeyAsync(
                "/api/v1/daily-entries", Guid.NewGuid().ToString(),
                EntryBody(farmId, flock, date, [new { eggGradeId = grades["Large"], quantity = 100 }]));

        var list = await client.GetFromJsonAsync<List<EntryDto>>(
            $"/api/v1/daily-entries?flockId={flockA}");

        Assert.Equal(2, list!.Count);
        Assert.All(list, e => Assert.Equal(flockA, e.FlockId));
        Assert.True(list[0].Date >= list[1].Date);
    }

    [Fact]
    public async Task Stock_AggregatesByGrade_SeparatesRestricted()
    {
        var (client, accountId, farmId, grades) = await SetupAsync("Large", "Medium");
        var flockId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

        // Two submitted entries -> lots: Large 600 + 400, Medium 300.
        foreach (var (date, gradeQty) in new (DateOnly, object[])[]
        {
            (today.AddDays(-1), [new { eggGradeId = grades["Large"], quantity = 600 },
                                 new { eggGradeId = grades["Medium"], quantity = 300 }]),
            (today, [new { eggGradeId = grades["Large"], quantity = 400 }]),
        })
        {
            var create = await client.PostWithKeyAsync(
                "/api/v1/daily-entries", Guid.NewGuid().ToString(),
                EntryBody(farmId, flockId, date, gradeQty));
            var id = (await create.Content.ReadFromJsonAsync<IdDto>())!.Id;
            await client.PostWithKeyAsync($"/api/v1/daily-entries/{id}/submit", Guid.NewGuid().ToString());
        }

        // Plus a restricted lot of Large (withdrawal for another week).
        await factory.SeedEggLotAsync(accountId, grades["Large"], 50,
            DateOnly.FromDateTime(DateTime.UtcNow.Date).AddDays(7));

        var stock = await client.GetFromJsonAsync<List<StockDto>>("/api/v1/stock");

        var large = stock!.Single(r => r.EggGradeId == grades["Large"]);
        Assert.Equal(1000, large.Available);
        Assert.Equal(50, large.Restricted);
        Assert.Equal("Large", large.GradeName);

        var medium = stock.Single(r => r.EggGradeId == grades["Medium"]);
        Assert.Equal(300, medium.Available);
        Assert.Equal(0, medium.Restricted);
    }

    [Fact]
    public async Task Stock_IsTenantScoped()
    {
        var (_, accountA, _, gradesA) = await SetupAsync("Large");
        await factory.SeedEggLotAsync(accountA, gradesA["Large"], 500);

        var emailB = $"b-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(emailB);
        var clientB = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(emailB));

        var stock = await clientB.GetFromJsonAsync<List<StockDto>>("/api/v1/stock");
        Assert.Empty(stock!);
    }

    [Fact]
    public async Task GetSalesOrder_ReturnsItems_ListFiltersByStatus()
    {
        var (client, accountId, _, grades) = await SetupAsync("Large");
        await factory.SeedEggLotAsync(accountId, grades["Large"], 500);
        var draftId = await factory.SeedSalesOrderAsync(accountId, grades["Large"], 100);
        var confirmedId = await factory.SeedSalesOrderAsync(accountId, grades["Large"], 200);
        await client.PostWithKeyAsync($"/api/v1/sales/{confirmedId}/confirm", Guid.NewGuid().ToString());

        var order = await client.GetFromJsonAsync<OrderDto>($"/api/v1/sales/{confirmedId}");
        Assert.Equal("Confirmed", order!.Status);
        var item = Assert.Single(order.Items);
        Assert.Equal(grades["Large"], item.EggGradeId);
        Assert.Equal(200, item.Quantity);

        var drafts = await client.GetFromJsonAsync<List<OrderDto>>("/api/v1/sales?status=draft");
        Assert.Contains(drafts!, o => o.Id == draftId);
        Assert.DoesNotContain(drafts!, o => o.Id == confirmedId);

        var bad = await client.GetAsync("/api/v1/sales?status=nonsense");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task GetForeignDailyEntry_Returns404()
    {
        var (clientA, _, farmA, gradesA) = await SetupAsync("Large");
        var create = await clientA.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(),
            EntryBody(farmA, Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow.Date),
                [new { eggGradeId = gradesA["Large"], quantity = 100 }]));
        var id = (await create.Content.ReadFromJsonAsync<IdDto>())!.Id;

        var emailB = $"b-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(emailB);
        var clientB = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(emailB));

        var response = await clientB.GetAsync($"/api/v1/daily-entries/{id}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
