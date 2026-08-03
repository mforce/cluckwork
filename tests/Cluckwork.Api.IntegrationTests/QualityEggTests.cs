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

    // ---- resolution and lot creation -----------------------------------

    private async Task<Guid> SubmitAsync(HttpClient client, Guid farmId, Guid flockId, Guid manualGradeId)
    {
        var entryId = (await (await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(),
            Body(farmId, flockId, [new { eggGradeId = manualGradeId, quantity = 600 }])))
            .Content.ReadFromJsonAsync<RecordedDto>())!.Id;

        var submit = await client.PostWithKeyAsync(
            $"/api/v1/daily-entries/{entryId}/submit", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
        return entryId;
    }

    [Fact]
    public async Task Submitting_turns_a_saleable_condition_counter_into_its_own_lot()
    {
        var (client, accountId, farmId, flockId, manual, crackedId, dirtyId) = await SetupAsync();

        var entryId = await SubmitAsync(client, farmId, flockId, manual["Large"]);

        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var entry = await db.DailyEntries.IgnoreQueryFilters().SingleAsync(e => e.Id == entryId);
            Assert.Equal(crackedId, entry.CrackedGradeId);
            Assert.Equal(dirtyId, entry.DirtyGradeId);

            var lots = await db.EggLots.IgnoreQueryFilters()
                .Where(l => l.DailyEntryId == entryId).ToListAsync();

            // 600 manual + 10 cracked + 5 dirty. Discarded is never stock.
            Assert.Equal(3, lots.Count);
            Assert.Equal(10, Assert.Single(lots, l => l.EggGradeId == crackedId).QuantityProduced);
            Assert.Equal(5, Assert.Single(lots, l => l.EggGradeId == dirtyId).QuantityProduced);
        });
    }

    // Voiding is the destructive path, and the one where "condition lots are
    // just lots" has to actually hold. VoidDailyEntryHandler reverses whatever
    // GetByDailyEntryLockedAsync returns — every lot linked to the entry,
    // grade-agnostic — so the condition lots are covered by construction rather
    // than by a second code path. That is worth pinning precisely BECAUSE it is
    // implicit: a future narrowing of that query to the entry's grade LINES
    // (the shape AdjustDailyEntryHandler originally had, and the bug this PR
    // fixed there) would leave a voided day carrying live, sellable cracked and
    // dirty stock with no entry standing behind it — phantom inventory, and
    // silent, since nothing else looks at a voided entry again.
    [Fact]
    public async Task Voiding_empties_the_condition_lots_too()
    {
        var (client, accountId, farmId, flockId, manual, crackedId, dirtyId) = await SetupAsync();
        var manualGradeId = manual["Large"];

        var entryId = await SubmitAsync(client, farmId, flockId, manualGradeId);
        var version = (await StateOf(accountId, entryId)).Version;

        var response = await client.PostWithKeyAsync(
            $"/api/v1/daily-entries/{entryId}/void", Guid.NewGuid().ToString(),
            new { version, reason = "recount void" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var lots = await db.EggLots.IgnoreQueryFilters()
                .Where(l => l.DailyEntryId == entryId).ToListAsync();

            // All three lots survive as rows (#60 — nothing is deleted, only
            // emptied), and every one of them is now at zero. Asserting the
            // manual lot alongside the two condition ones is what makes this a
            // test of the WHOLE reversal rather than of the condition half:
            // a change that emptied only the condition lots would be just as
            // wrong, and would pass an assertion that named them alone.
            Assert.Equal(3, lots.Count);
            Assert.Equal(0, Assert.Single(lots, l => l.EggGradeId == manualGradeId).QuantityProduced);
            Assert.Equal(0, Assert.Single(lots, l => l.EggGradeId == crackedId).QuantityProduced);
            Assert.Equal(0, Assert.Single(lots, l => l.EggGradeId == dirtyId).QuantityProduced);

            // The eggs left on the ledger, not merely off the lot (#101). One
            // Void movement per lot that had stock, so a reversal that zeroed a
            // quantity without writing the movement is caught here rather than
            // showing up later as stock that cannot be reconciled.
            var lotIds = lots.Select(l => l.Id).ToList();
            var voided = await db.EggInventoryMovements.IgnoreQueryFilters()
                .Where(m => m.MovementType == EggMovementType.Void && lotIds.Contains(m.EggLotId))
                .ToListAsync();
            Assert.Equal(3, voided.Count);
            Assert.Equal(-615, voided.Sum(m => m.QuantityDelta)); // 600 + 10 + 5
        });
    }

    [Fact]
    public async Task A_non_saleable_condition_stays_a_loss()
    {
        var (client, accountId, farmId, flockId, manual, crackedId, _) =
            await SetupAsync(crackedSaleable: false);

        var entryId = await SubmitAsync(client, farmId, flockId, manual["Large"]);

        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var entry = await db.DailyEntries.IgnoreQueryFilters().SingleAsync(e => e.Id == entryId);
            Assert.Null(entry.CrackedGradeId);
            Assert.Empty(await db.EggLots.IgnoreQueryFilters()
                .Where(l => l.DailyEntryId == entryId && l.EggGradeId == crackedId).ToListAsync());
        });
    }

    // The case a saleability-only rule gets wrong. EggGrade.Deactivate() leaves
    // IsSaleable set, so "inactive but saleable" is an ordinary reachable state
    // — reached by an owner retiring a condition grade without first clearing
    // its saleability. Resolving on IsSaleable alone would mint stock under a
    // grade the farm has already removed from capture.
    [Fact]
    public async Task An_inactive_but_saleable_condition_grade_resolves_to_nothing()
    {
        var (client, accountId, farmId, flockId, manual, crackedId, _) =
            await SetupAsync(crackedSaleable: true, crackedActive: false);

        var entryId = await SubmitAsync(client, farmId, flockId, manual["Large"]);

        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var grade = await db.EggGrades.IgnoreQueryFilters().SingleAsync(g => g.Id == crackedId);
            Assert.False(grade.Active);
            Assert.True(grade.IsSaleable, "the fixture must keep the trap: inactive yet still saleable");

            var entry = await db.DailyEntries.IgnoreQueryFilters().SingleAsync(e => e.Id == entryId);
            Assert.Null(entry.CrackedGradeId);
            Assert.Empty(await db.EggLots.IgnoreQueryFilters()
                .Where(l => l.DailyEntryId == entryId && l.EggGradeId == crackedId).ToListAsync());
        });
    }

    // A zero counter still RESOLVES — the snapshot records that the farm was
    // selling cracked eggs that day — but mints no lot, because EggLot.Create
    // rejects a zero quantity. Snapshot and lot are separate decisions and this
    // is the case that proves it.
    [Fact]
    public async Task A_zero_counter_is_snapshotted_but_creates_no_lot()
    {
        var (client, accountId, farmId, flockId, manual, crackedId, _) = await SetupAsync();

        var entryId = (await (await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(),
            new
            {
                farmId,
                houseId = Guid.NewGuid(),
                flockId,
                date = DateOnly.FromDateTime(DateTime.UtcNow.Date),
                totalEggs = 608,
                crackedEggs = 0,
                dirtyEggs = 5,
                discardedEggs = 3,
                mortalityCount = 0,
                grades = new[] { new { eggGradeId = manual["Large"], quantity = 600 } }
            })).Content.ReadFromJsonAsync<RecordedDto>())!.Id;

        Assert.Equal(HttpStatusCode.OK, (await client.PostWithKeyAsync(
            $"/api/v1/daily-entries/{entryId}/submit", Guid.NewGuid().ToString())).StatusCode);

        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var entry = await db.DailyEntries.IgnoreQueryFilters().SingleAsync(e => e.Id == entryId);
            Assert.Equal(crackedId, entry.CrackedGradeId);
            Assert.Empty(await db.EggLots.IgnoreQueryFilters()
                .Where(l => l.DailyEntryId == entryId && l.EggGradeId == crackedId).ToListAsync());
        });
    }

    // ---- adjustment reconciles the quality lots too ---------------------
    //
    // The manual lines and the condition counters produce the same KIND of
    // thing — a lot linked to this entry — so an adjustment has to reconcile
    // both. Getting this wrong is not a missing feature but active damage: the
    // reconciler drives every linked lot toward a target, and a condition lot
    // absent from the target set is driven to ZERO, silently destroying stock
    // the farm holds.

    private async Task<int> CrackedLotQuantity(Guid accountId, Guid entryId, Guid crackedId)
    {
        var quantity = -1;
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var lot = await db.EggLots.IgnoreQueryFilters()
                .SingleOrDefaultAsync(l => l.DailyEntryId == entryId && l.EggGradeId == crackedId);
            quantity = lot?.QuantityProduced ?? 0;
        });
        return quantity;
    }

    private Task<HttpResponseMessage> AdjustAsync(
        HttpClient client, Guid entryId, int version, int cracked, int total) =>
        client.PostWithKeyAsync(
            $"/api/v1/daily-entries/{entryId}/adjust", Guid.NewGuid().ToString(),
            new
            {
                version,
                totalEggs = total,
                crackedEggs = cracked,
                dirtyEggs = 5,
                discardedEggs = 3,
                mortalityCount = 0,
                reason = "recount",
                grades = new[] { new { eggGradeId = ManualGradeId, quantity = 600 } }
            });

    private Guid ManualGradeId;

    [Fact]
    public async Task Adjusting_a_condition_counter_upward_grows_its_lot()
    {
        var (client, accountId, farmId, flockId, manual, crackedId, _) = await SetupAsync();
        ManualGradeId = manual["Large"];

        var entryId = await SubmitAsync(client, farmId, flockId, ManualGradeId);
        Assert.Equal(10, await CrackedLotQuantity(accountId, entryId, crackedId));

        var version = (await StateOf(accountId, entryId)).Version;
        var response = await AdjustAsync(client, entryId, version, cracked: 20, total: 628);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(20, await CrackedLotQuantity(accountId, entryId, crackedId));
    }

    [Fact]
    public async Task Adjusting_a_condition_counter_to_zero_empties_its_lot()
    {
        var (client, accountId, farmId, flockId, manual, crackedId, _) = await SetupAsync();
        ManualGradeId = manual["Large"];

        var entryId = await SubmitAsync(client, farmId, flockId, ManualGradeId);
        var version = (await StateOf(accountId, entryId)).Version;

        var response = await AdjustAsync(client, entryId, version, cracked: 0, total: 608);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, await CrackedLotQuantity(accountId, entryId, crackedId));
    }

    [Fact]
    public async Task Adjusting_leaves_the_manual_lot_alone_while_the_counter_changes()
    {
        // The other half: reconciling the condition lots must not disturb the
        // manual ones. A fix that simply overwrote the target set would pass
        // the two tests above and empty every manual lot.
        var (client, accountId, farmId, flockId, manual, crackedId, _) = await SetupAsync();
        ManualGradeId = manual["Large"];

        var entryId = await SubmitAsync(client, farmId, flockId, ManualGradeId);
        var version = (await StateOf(accountId, entryId)).Version;

        Assert.Equal(HttpStatusCode.OK,
            (await AdjustAsync(client, entryId, version, cracked: 20, total: 628)).StatusCode);

        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var manualLot = await db.EggLots.IgnoreQueryFilters()
                .SingleAsync(l => l.DailyEntryId == entryId && l.EggGradeId == ManualGradeId);
            Assert.Equal(600, manualLot.QuantityProduced);
        });
        Assert.Equal(20, await CrackedLotQuantity(accountId, entryId, crackedId));
    }

    // A day with no cracked eggs is an ordinary good day, and it has a snapshot
    // but NO lot. Adjusting such an entry puts a target of zero in front of the
    // new-lot loop with no lot to match it — and EggLot.Create rejects a zero
    // quantity, so without the skip the adjustment throws instead of applying.
    //
    // Written because the mutation check caught the guard surviving: every other
    // test here submits a positive counter, so the zero-with-no-lot path was
    // never exercised and the guard was, at that point, an untested claim.
    [Fact]
    public async Task Adjusting_a_day_that_never_had_cracked_eggs_still_succeeds()
    {
        var (client, accountId, farmId, flockId, manual, crackedId, _) = await SetupAsync();
        ManualGradeId = manual["Large"];

        var entryId = (await (await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(),
            new
            {
                farmId,
                houseId = Guid.NewGuid(),
                flockId,
                date = DateOnly.FromDateTime(DateTime.UtcNow.Date),
                totalEggs = 608,
                crackedEggs = 0,
                dirtyEggs = 5,
                discardedEggs = 3,
                mortalityCount = 0,
                grades = new[] { new { eggGradeId = ManualGradeId, quantity = 600 } }
            })).Content.ReadFromJsonAsync<RecordedDto>())!.Id;

        Assert.Equal(HttpStatusCode.OK, (await client.PostWithKeyAsync(
            $"/api/v1/daily-entries/{entryId}/submit", Guid.NewGuid().ToString())).StatusCode);

        var version = (await StateOf(accountId, entryId)).Version;
        var response = await AdjustAsync(client, entryId, version, cracked: 0, total: 608);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, await CrackedLotQuantity(accountId, entryId, crackedId));
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
