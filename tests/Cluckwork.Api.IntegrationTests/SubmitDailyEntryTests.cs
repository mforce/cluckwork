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
    private sealed record EntryStatusDto(Guid Id, string Status);

    // total defaults to reconcile exactly against the single-grade-600 shape
    // most callers below use (600 sellable + 10 cracked + 5 dirty + 3
    // discarded = 618); #394 requires submit's grades to sum to EXACTLY
    // total-cracked-dirty-discarded, so a caller with a different grade sum
    // (e.g. two lines) must pass its own matching total.
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
        // 600 + 300 = 900 sellable; total must match (900 + 10 + 5 + 3 = 918).
        var entryId = await RecordAsync(client, Body(farmId, flockId,
        [
            new { eggGradeId = grades["Large"], quantity = 600 },
            new { eggGradeId = grades["Medium"], quantity = 300 }
        ], total: 918));

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

    // #394 — the bug as reported: an ungraded, no-loss entry used to submit
    // cleanly and silently produce zero stock for real production. A direct
    // API caller (no grades field at all) cannot submit a non-zero-sellable
    // entry any more than the SPA can.
    [Fact]
    public async Task Submit_NoGrades_NonZeroSellable_Fails_NoLotsCreated()
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

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("DailyEntry.GradesNotReconciled", await response.Content.ReadAsStringAsync());
        var lotCount = await factory.WithTenantScopeAsync(accountId, db => db.EggLots.CountAsync());
        Assert.Equal(0, lotCount);
        var entry = await client.GetFromJsonAsync<EntryStatusDto>($"/api/v1/daily-entries/{entryId}");
        Assert.Equal("Draft", entry!.Status); // refusal leaves the entry untouched
    }

    // A caller cannot dodge the rule by grading SOME of the day either — a
    // direct API caller who grades less than sellable is refused exactly like
    // one who grades nothing.
    [Fact]
    public async Task Submit_PartialGrades_Fails_NoLotsCreated()
    {
        var (client, accountId, farmId, flockId, grades) = await SetupAsync("Large");
        // Body's fixed losses (10 cracked + 5 dirty + 3 discarded = 18) plus a
        // 500 target sellable = 518 total; only 300 of that 500 is graded.
        var entryId = await RecordAsync(client, Body(farmId, flockId,
            [new { eggGradeId = grades["Large"], quantity = 300 }], total: 518));

        var response = await client.PostWithKeyAsync(
            $"/api/v1/daily-entries/{entryId}/submit", Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var lotCount = await factory.WithTenantScopeAsync(accountId, db => db.EggLots.CountAsync());
        Assert.Equal(0, lotCount);
    }

    // The fourth case named in the issue: every egg accounted for as a loss,
    // so zero sellable eggs validly reconciles to zero grade lines — submit
    // succeeds and (correctly) creates no lots.
    [Fact]
    public async Task Submit_ZeroSellableDay_NoGrades_Succeeds_NoLots()
    {
        var (client, accountId, farmId, flockId, _) = await SetupAsync("Large");
        var entryId = await RecordAsync(client, new
        {
            farmId,
            houseId = Guid.NewGuid(),
            flockId,
            date = DateOnly.FromDateTime(DateTime.UtcNow.Date),
            totalEggs = 50, crackedEggs = 20, dirtyEggs = 20, discardedEggs = 10, mortalityCount = 0,
        });

        var response = await client.PostWithKeyAsync(
            $"/api/v1/daily-entries/{entryId}/submit", Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var submitted = await response.Content.ReadFromJsonAsync<SubmitDto>();
        Assert.Equal("Submitted", submitted!.Status);
        Assert.Empty(submitted.EggLotIds);
        var lotCount = await factory.WithTenantScopeAsync(accountId, db => db.EggLots.CountAsync());
        Assert.Equal(0, lotCount);
    }

    // The control: a DRAFT save with no or partial grades still succeeds —
    // proves the invariant gates SUBMIT, not RECORD/save.
    [Fact]
    public async Task Record_NoOrPartialGrades_AsDraft_StillSucceeds()
    {
        var (client, _, farmId, flockId, grades) = await SetupAsync("Large");

        var noGrades = await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(), new
            {
                farmId, houseId = Guid.NewGuid(), flockId,
                date = DateOnly.FromDateTime(DateTime.UtcNow.Date),
                totalEggs = 500, crackedEggs = 0, dirtyEggs = 0, discardedEggs = 0, mortalityCount = 0,
            });
        Assert.Equal(HttpStatusCode.Created, noGrades.StatusCode);

        var partialGrades = await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(), new
            {
                farmId, houseId = Guid.NewGuid(), flockId,
                date = DateOnly.FromDateTime(DateTime.UtcNow.Date).AddDays(-1),
                totalEggs = 500, crackedEggs = 0, dirtyEggs = 0, discardedEggs = 0, mortalityCount = 0,
                grades = new[] { new { eggGradeId = grades["Large"], quantity = 300 } }
            });
        Assert.Equal(HttpStatusCode.Created, partialGrades.StatusCode);
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

    // #494 — creation wasn't on the audit trail at all; only corrections were.
    [Fact]
    public async Task DailyEntry_Record_WritesCreateAuditEvent()
    {
        var (client, accountId, farmId, flockId, grades) = await SetupAsync("Large");
        var entryId = await RecordAsync(client, Body(farmId, flockId,
            [new { eggGradeId = grades["Large"], quantity = 600 }]));

        var events = await factory.WithTenantScopeAsync(accountId, db => db.AuditEvents
            .Where(e => e.EntityType == "DailyEntry" && e.EntityId == entryId)
            .ToListAsync());

        var created = Assert.Single(events);
        Assert.Equal("DailyEntry.Create", created.Action);
    }

    // The gate: re-recording against the same natural key APPENDS to the
    // existing entry — that is not a creation and must not emit a second event.
    [Fact]
    public async Task DailyEntry_ReRecordedOnTheSameKey_WritesOnlyOneCreateEvent()
    {
        var (client, accountId, farmId, flockId, grades) = await SetupAsync("Large");
        var houseId = Guid.NewGuid();
        var date = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        object Body2(int total, int quantity) => new
        {
            farmId,
            houseId,
            flockId,
            date,
            totalEggs = total,
            crackedEggs = 10,
            dirtyEggs = 5,
            discardedEggs = 3,
            mortalityCount = 0,
            grades = new object[] { new { eggGradeId = grades["Large"], quantity } },
        };

        var entryId = await RecordAsync(client, Body2(618, 600));
        var again = await RecordAsync(client, Body2(718, 700));
        Assert.Equal(entryId, again);

        var events = await factory.WithTenantScopeAsync(accountId, db => db.AuditEvents
            .Where(e => e.EntityType == "DailyEntry" && e.EntityId == entryId)
            .Select(e => e.Action)
            .ToListAsync());

        // The re-record DOES leave a trace now — a draft edit is attributable,
        // so that rewriting a colleague's numbers before submission is visible
        // (#494). What must never happen is a second CREATION, which would give
        // the entry two candidate authors and let the later one win.
        Assert.Equal(["DailyEntry.Create", "DailyEntry.Update"], events);
        Assert.Single(events, a => a == "DailyEntry.Create");
    }
}
