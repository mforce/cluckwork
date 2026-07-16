namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

// #8 — the production -> stock bridge. Submitting a daily entry generates one
// egg lot per grade line; the whole MVP loop hangs off this.
[Collection(IntegrationCollection.Name)]
public sealed class SubmitDailyEntryTests(CluckworkWebApplicationFactory factory)
{
    private sealed record RecordedDto(Guid Id);
    private sealed record SubmitDto(Guid Id, string Status, List<Guid> EggLotIds);

    private static object Body(Guid farmId, Guid flockId, object[] grades) => new
    {
        farmId,
        houseId = Guid.NewGuid(),
        flockId,
        date = DateOnly.FromDateTime(DateTime.UtcNow.Date),
        totalEggs = 1000,
        crackedEggs = 10,
        dirtyEggs = 5,
        discardedEggs = 3,
        mortalityCount = 0,
        grades
    };

    private async Task<(HttpClient Client, Guid AccountId, Guid FarmId, Guid FlockId, Dictionary<string, Guid> Grades)>
        SetupAsync(params string[] gradeNames)
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var farmId = Guid.NewGuid();
        var grades = await factory.SeedEggGradesAsync(accountId, farmId, gradeNames);
        var flockId = await factory.SeedFlockAsync(accountId, farmId);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        return (client, accountId, farmId, flockId, grades);
    }

    private static async Task<Guid> RecordAsync(HttpClient client, object body)
    {
        var response = await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(), body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RecordedDto>())!.Id;
    }

    [Fact]
    public async Task Submit_GeneratesOneLotPerGradeLine()
    {
        var (client, accountId, farmId, flockId, grades) = await SetupAsync("Large", "Medium");
        var entryId = await RecordAsync(client, Body(farmId, flockId,
        [
            new { eggGradeId = grades["Large"], quantity = 600 },
            new { eggGradeId = grades["Medium"], quantity = 300 }
        ]));

        var response = await client.PostWithKeyAsync(
            $"/api/v1/daily-entries/{entryId}/submit", Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var submitted = await response.Content.ReadFromJsonAsync<SubmitDto>();
        Assert.Equal("Submitted", submitted!.Status);
        Assert.Equal(2, submitted.EggLotIds.Count);

        var lots = await factory.WithTenantScopeAsync(accountId, db => db.EggLots.ToListAsync());
        Assert.Equal(2, lots.Count);
        var large = lots.Single(l => l.EggGradeId == grades["Large"]);
        Assert.Equal(600, large.QuantityProduced);
        Assert.Equal(600, large.QuantityAvailable);
        Assert.Equal(flockId, large.FlockId);
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow.Date), large.ProductionDate);
        Assert.All(lots, l => Assert.Equal(accountId, l.AccountId));
    }

    [Fact]
    public async Task Submit_Twice_SecondRejected_NoDuplicateLots()
    {
        var (client, accountId, farmId, flockId, grades) = await SetupAsync("Large");
        var entryId = await RecordAsync(client, Body(farmId, flockId,
            [new { eggGradeId = grades["Large"], quantity = 600 }]));

        var first = await client.PostWithKeyAsync(
            $"/api/v1/daily-entries/{entryId}/submit", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await client.PostWithKeyAsync(
            $"/api/v1/daily-entries/{entryId}/submit", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);

        var lotCount = await factory.WithTenantScopeAsync(accountId, db => db.EggLots.CountAsync());
        Assert.Equal(1, lotCount);
    }

    [Fact]
    public async Task ParallelSubmits_ExactlyOneWins_NoDuplicateLots()
    {
        // The race both reviews flagged: two concurrent submits with different
        // idempotency keys. The Version bump in Submit() makes the loser's UPDATE
        // miss (409 via the global concurrency mapping); its lots roll back.
        var (client, accountId, farmId, flockId, grades) = await SetupAsync("Large");
        var entryId = await RecordAsync(client, Body(farmId, flockId,
            [new { eggGradeId = grades["Large"], quantity = 600 }]));

        var a = client.PostWithKeyAsync($"/api/v1/daily-entries/{entryId}/submit", Guid.NewGuid().ToString());
        var b = client.PostWithKeyAsync($"/api/v1/daily-entries/{entryId}/submit", Guid.NewGuid().ToString());
        var responses = await Task.WhenAll(a, b);

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, responses.Count(r =>
            r.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity));

        var lotCount = await factory.WithTenantScopeAsync(accountId, db => db.EggLots.CountAsync());
        Assert.Equal(1, lotCount);
    }

    [Fact]
    public async Task ReRecord_AfterSubmit_Rejected()
    {
        // Submitted entries are immutable to the record endpoint: their grade
        // lines already became lots, and silent edits would diverge from stock.
        var (client, _, farmId, flockId, grades) = await SetupAsync("Large");
        var body = Body(farmId, flockId, [new { eggGradeId = grades["Large"], quantity = 600 }]);
        var entryId = await RecordAsync(client, body);

        await client.PostWithKeyAsync($"/api/v1/daily-entries/{entryId}/submit", Guid.NewGuid().ToString());

        var reRecord = await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(), body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reRecord.StatusCode);
    }

    [Fact]
    public async Task Submit_WithoutGrades_Succeeds_NoLots()
    {
        var (client, accountId, farmId, flockId, _) = await SetupAsync("Large");
        var entryId = await RecordAsync(client, new
        {
            farmId,
            houseId = Guid.NewGuid(),
            flockId,
            date = DateOnly.FromDateTime(DateTime.UtcNow.Date),
            totalEggs = 500, crackedEggs = 0, dirtyEggs = 0, discardedEggs = 0, mortalityCount = 0
        });

        var response = await client.PostWithKeyAsync(
            $"/api/v1/daily-entries/{entryId}/submit", Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lotCount = await factory.WithTenantScopeAsync(accountId, db => db.EggLots.CountAsync());
        Assert.Equal(0, lotCount);
    }

    [Fact]
    public async Task Submit_ForeignEntry_Returns404()
    {
        var (clientA, _, farmA, flockA, gradesA) = await SetupAsync("Large");
        var entryId = await RecordAsync(clientA, Body(farmA, flockA,
            [new { eggGradeId = gradesA["Large"], quantity = 100 }]));

        var emailB = $"b-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(emailB);
        var clientB = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(emailB));

        var response = await clientB.PostWithKeyAsync(
            $"/api/v1/daily-entries/{entryId}/submit", Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task FullLoop_RecordSubmitSellConfirm_StockDecrements()
    {
        // The MVP loop end-to-end: daily entry -> submit -> lots -> sale -> FIFO
        // allocation -> stock decremented.
        var (client, accountId, farmId, flockId, grades) = await SetupAsync("Large");
        var entryId = await RecordAsync(client, Body(farmId, flockId,
            [new { eggGradeId = grades["Large"], quantity = 600 }]));

        var submit = await client.PostWithKeyAsync(
            $"/api/v1/daily-entries/{entryId}/submit", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);

        var orderId = await factory.SeedSalesOrderAsync(accountId, grades["Large"], 250);
        var confirm = await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);

        var lot = await factory.WithTenantScopeAsync(accountId, db => db.EggLots.SingleAsync());
        Assert.Equal(600, lot.QuantityProduced);
        Assert.Equal(350, lot.QuantityAvailable);
    }
}
