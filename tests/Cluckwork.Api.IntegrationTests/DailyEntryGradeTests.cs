namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

// #6: daily entry captures sellable production by grade, referencing EggGrade
// rows (spec §9.1/9.2). Covers the HTTP round-trip, full-replace semantics on
// re-record (in-place reconcile, unique (DailyEntryId, EggGradeId) index), and
// grade-reference validation, against real Postgres.
[Collection(IntegrationCollection.Name)]
public sealed class DailyEntryGradeTests(CluckworkWebApplicationFactory factory)
{
    private static object Body(Guid farmId, Guid houseId, Guid flockId, object[] grades, int total = 1000) => new
    {
        farmId,
        houseId,
        flockId,
        date = DateOnly.FromDateTime(DateTime.UtcNow.Date),
        totalEggs = total,
        crackedEggs = 10,
        dirtyEggs = 5,
        discardedEggs = 3,
        mortalityCount = 0,
        grades
    };

    [Fact]
    public async Task Record_WithGrades_PersistsLines()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var grades = await factory.SeedEggGradesAsync(accountId, "Large", "Medium");
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        var (farmId, houseId, flockId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var response = await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(),
            Body(farmId, houseId, flockId,
            [
                new { eggGradeId = grades["Large"], quantity = 600 },
                new { eggGradeId = grades["Medium"], quantity = 300 }
            ]));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var lines = await factory.WithTenantScopeAsync(accountId, db =>
            db.DailyEntryGrades.ToListAsync());
        Assert.Equal(2, lines.Count);
        Assert.Equal(600, lines.Single(g => g.EggGradeId == grades["Large"]).Quantity);
        Assert.All(lines, g => Assert.Equal(accountId, g.AccountId));
    }

    [Fact]
    public async Task ReRecord_ReplacesGradeLines()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var grades = await factory.SeedEggGradesAsync(accountId, "Large", "Small");
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        var (farmId, houseId, flockId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var first = await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(),
            Body(farmId, houseId, flockId, [new { eggGradeId = grades["Large"], quantity = 600 }]));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // Same natural key, new Idempotency-Key -> upsert path replaces the lines.
        var second = await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(),
            Body(farmId, houseId, flockId,
            [
                new { eggGradeId = grades["Large"], quantity = 550 },
                new { eggGradeId = grades["Small"], quantity = 100 }
            ], total: 900));
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        var lines = await factory.WithTenantScopeAsync(accountId, db =>
            db.DailyEntryGrades.ToListAsync());
        Assert.Equal(2, lines.Count);
        Assert.Equal(550, lines.Single(g => g.EggGradeId == grades["Large"]).Quantity);
        Assert.Equal(100, lines.Single(g => g.EggGradeId == grades["Small"]).Quantity);
    }

    [Fact]
    public async Task ReRecord_WithoutGrades_PreservesLines()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var grades = await factory.SeedEggGradesAsync(accountId, "Large");
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        var (farmId, houseId, flockId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(),
            Body(farmId, houseId, flockId, [new { eggGradeId = grades["Large"], quantity = 600 }]));

        // Older client shape: no grades field at all.
        var second = await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(), new
            {
                farmId, houseId, flockId,
                date = DateOnly.FromDateTime(DateTime.UtcNow.Date),
                totalEggs = 950, crackedEggs = 10, dirtyEggs = 5, discardedEggs = 3, mortalityCount = 1
            });
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        var line = Assert.Single(await factory.WithTenantScopeAsync(accountId, db =>
            db.DailyEntryGrades.ToListAsync()));
        Assert.Equal(600, line.Quantity);
    }

    [Fact]
    public async Task GradesExceedingSellable_Rejected()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var grades = await factory.SeedEggGradesAsync(accountId, "Large");
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        var response = await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(),
            Body(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                [new { eggGradeId = grades["Large"], quantity = 2000 }]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UnknownGradeId_Rejected()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        var response = await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(),
            Body(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                [new { eggGradeId = Guid.NewGuid(), quantity = 100 }]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task ForeignTenantGradeId_Rejected()
    {
        // Account B may not record production against account A's grade rows.
        var emailA = $"a-{Guid.NewGuid():N}@test.local";
        var accountA = await factory.SeedAccountWithUserAsync(emailA);
        var gradesA = await factory.SeedEggGradesAsync(accountA, "Large");

        var emailB = $"b-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(emailB);
        var clientB = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(emailB));

        var response = await clientB.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(),
            Body(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                [new { eggGradeId = gradesA["Large"], quantity = 100 }]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task NullGradeElement_Rejected()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        var response = await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(),
            Body(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), [null!]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ListEggGrades_ReturnsTenantGradesInOrder()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        await factory.SeedEggGradesAsync(accountId, "Jumbo", "Large");
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        var list = await client.GetFromJsonAsync<List<EggGradeDto>>("/api/v1/egg-grades");

        Assert.Equal(2, list!.Count);
        Assert.Equal(["Jumbo", "Large"], list.Select(g => g.Name).ToArray());
        Assert.All(list, g => Assert.True(g.IsSaleable));
    }

    private sealed record EggGradeDto(Guid Id, string Name, string GradeType, int SortOrder, bool IsSaleable);
}
