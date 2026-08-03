namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Eggs;
using Microsoft.EntityFrameworkCore;

// #396 — Cracked and Dirty become saleable stock through their own counters.
//
// The rule these tests exist to hold: a condition grade is fed by its COUNTER
// and never by a manual grade line. Excluding it from the Grading pane is a UI
// affordance; the enforcement has to be server-side, because the eligibility
// check a manual line passes is "active and saleable" with no kind restriction —
// and this feature makes Cracked and Dirty saleable. So a direct or stale API
// client can name a Cracked id as a manual line, the exact-total check passes
// (it is only a sum), and submission then creates BOTH that manual lot and the
// counter-backed one: two lots for one grade on one day, double-counting the
// day's stock and breaking the one-lot-per-grade assumption reconciliation
// depends on.
[Collection(IntegrationCollection.Name)]
public sealed class QualityEggTests(CluckworkWebApplicationFactory factory)
{
    private sealed record RecordedDto(Guid Id);

    private static object Body(Guid farmId, Guid flockId, object[] grades, int total = 618) => new
    {
        farmId,
        houseId = Guid.NewGuid(),
        flockId,
        date = DateOnly.FromDateTime(DateTime.UtcNow.Date),
        totalEggs = total,
        crackedEggs = 10,
        dirtyEggs = 5,
        discardedEggs = 3,
        mortalityCount = 0,
        grades
    };

    private async Task<(HttpClient Client, Guid AccountId, Guid FarmId, Guid FlockId,
        Dictionary<string, Guid> Manual, Guid CrackedId, Guid DirtyId)> SetupAsync(
        bool crackedSaleable = true, bool crackedActive = true)
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var farmId = Guid.NewGuid();
        var manual = await factory.SeedEggGradesAsync(accountId, farmId, "Large");
        var flockId = await factory.SeedFlockAsync(accountId, farmId);

        var crackedId = Guid.NewGuid();
        var dirtyId = Guid.NewGuid();
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var cracked = EggGrade.Create(
                crackedId, accountId, farmId, "Cracked", EggGradeType.Quality,
                sortOrder: 50, isSaleable: crackedSaleable,
                dailyEntryKind: DailyEntryKind.Cracked);
            if (!crackedActive) cracked.Deactivate();
            db.EggGrades.Add(cracked);

            db.EggGrades.Add(EggGrade.Create(
                dirtyId, accountId, farmId, "Dirty", EggGradeType.Quality,
                sortOrder: 51, isSaleable: true,
                dailyEntryKind: DailyEntryKind.Dirty));
            await db.SaveChangesAsync();
        });

        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        return (client, accountId, farmId, flockId, manual, crackedId, dirtyId);
    }

    [Fact]
    public async Task Recording_a_manual_line_naming_a_condition_grade_is_refused()
    {
        var (client, _, farmId, flockId, _, crackedId, _) = await SetupAsync();

        // 600 named against the CRACKED grade as a manual line. The sum still
        // reconciles (600 + 10 + 5 + 3 = 618), which is exactly why the total
        // check cannot catch this.
        var response = await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(),
            Body(farmId, flockId, [new { eggGradeId = crackedId, quantity = 600 }]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("ConditionGradeNotManual", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_condition_line_writes_nothing()
    {
        var (client, accountId, farmId, flockId, _, crackedId, _) = await SetupAsync();

        await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(),
            Body(farmId, flockId, [new { eggGradeId = crackedId, quantity = 600 }]));

        // Not "the response was 422" — the durable state. A refusal that still
        // created a draft would leave the day half-recorded under a grade the
        // farm cannot see in its Grading pane.
        await factory.WithTenantScopeAsync(accountId, async db =>
            Assert.Empty(await db.DailyEntries.IgnoreQueryFilters()
                .Where(e => e.FlockId == flockId).ToListAsync()));
    }

    [Fact]
    public async Task Adjusting_onto_a_condition_grade_is_refused_and_leaves_the_entry_intact()
    {
        var (client, accountId, farmId, flockId, manual, crackedId, _) = await SetupAsync();

        var entryId = (await (await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(),
            Body(farmId, flockId, [new { eggGradeId = manual["Large"], quantity = 600 }])))
            .Content.ReadFromJsonAsync<RecordedDto>())!.Id;

        await client.PostWithKeyAsync($"/api/v1/daily-entries/{entryId}/submit", Guid.NewGuid().ToString());

        var before = await StateOf(accountId, entryId);

        var response = await client.PostWithKeyAsync(
            $"/api/v1/daily-entries/{entryId}/adjust", Guid.NewGuid().ToString(),
            new
            {
                // Adjust carries the entry's optimistic version. Sent correctly
                // so the refusal under test is the condition-grade guard and not
                // a 409 the request would have earned anyway.
                version = before.Version,
                totalEggs = 618,
                crackedEggs = 10,
                dirtyEggs = 5,
                discardedEggs = 3,
                mortalityCount = 0,
                reason = "recount",
                grades = new[] { new { eggGradeId = crackedId, quantity = 600 } }
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        // Adjust is the path that already generated lots, so a partial failure
        // here is worse than on record: the entry must keep its status, its
        // version, and its lot set exactly.
        Assert.Equal(before, await StateOf(accountId, entryId));
    }

    private async Task<(string Status, int Version, int Lots)> StateOf(Guid accountId, Guid entryId)
    {
        (string, int, int) state = default;
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var entry = await db.DailyEntries.IgnoreQueryFilters().SingleAsync(e => e.Id == entryId);
            var lots = await db.EggLots.IgnoreQueryFilters().CountAsync(l => l.DailyEntryId == entryId);
            state = (entry.Status.ToString(), entry.Version, lots);
        });
        return state;
    }
}
