namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

// #6: daily entry captures sellable production by grade. Covers the HTTP
// round-trip and the full-replace semantics on re-record (orphan-deleted lines,
// unique (DailyEntryId, GradeCode) index) against real Postgres.
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
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        var (farmId, houseId, flockId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var response = await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(),
            Body(farmId, houseId, flockId,
                [new { gradeCode = "A-Large", quantity = 600 }, new { gradeCode = "A-Medium", quantity = 300 }]));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var lines = await factory.WithTenantScopeAsync(accountId, db =>
            db.DailyEntryGrades.OrderBy(g => g.GradeCode).ToListAsync());
        Assert.Equal(2, lines.Count);
        Assert.Equal(600, lines.Single(g => g.GradeCode == "A-Large").Quantity);
        Assert.All(lines, g => Assert.Equal(accountId, g.AccountId));
    }

    [Fact]
    public async Task ReRecord_ReplacesGradeLines()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        var (farmId, houseId, flockId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var first = await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(),
            Body(farmId, houseId, flockId, [new { gradeCode = "A-Large", quantity = 600 }]));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // Same natural key, new Idempotency-Key -> upsert path replaces the lines.
        var second = await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(),
            Body(farmId, houseId, flockId,
                [new { gradeCode = "A-Large", quantity = 550 }, new { gradeCode = "B", quantity = 100 }], total: 900));
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        var lines = await factory.WithTenantScopeAsync(accountId, db =>
            db.DailyEntryGrades.OrderBy(g => g.GradeCode).ToListAsync());
        Assert.Equal(2, lines.Count);
        Assert.Equal(550, lines.Single(g => g.GradeCode == "A-Large").Quantity);
        Assert.Equal(100, lines.Single(g => g.GradeCode == "B").Quantity);
    }

    [Fact]
    public async Task GradesExceedingTotal_Rejected()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        var response = await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(),
            Body(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                [new { gradeCode = "A-Large", quantity = 2000 }]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
