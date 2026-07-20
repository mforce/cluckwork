namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #69 — adjusting/voiding submitted entries must keep the entry, its egg lots,
// and the bird ledger consistent in one transaction, and must never touch
// eggs that already left the farm (sold/allocated).
[Collection(IntegrationCollection.Name)]
public sealed class DailyEntryAdjustTests(CluckworkWebApplicationFactory factory)
{
    private sealed record Created(Guid Id);
    private sealed record GradeLine(Guid EggGradeId, int Quantity);
    private sealed record EntryDto(
        Guid Id, Guid FlockId, DateOnly Date, string Status,
        int TotalEggs, int CrackedEggs, int DirtyEggs, int DiscardedEggs, int MortalityCount,
        List<GradeLine> Grades, int Version, string? AdjustReason, string? VoidReason,
        DateTimeOffset? LockedAtUtc, JsonElement? AdjustedFrom);
    private sealed record AdjustDto(Guid Id, string Status, int Version);
    private sealed record FlockDto(Guid Id, int CurrentBirds);

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

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

    private static async Task<Guid> RecordAndSubmitAsync(
        HttpClient client, Guid farmId, Guid flockId, DateOnly date,
        int total, int mortality, params (Guid GradeId, int Quantity)[] lines)
    {
        var record = await client.PostWithKeyAsync("/api/v1/daily-entries", Guid.NewGuid().ToString(), new
        {
            farmId,
            houseId = Guid.NewGuid(),
            flockId,
            date,
            totalEggs = total,
            crackedEggs = 0,
            dirtyEggs = 0,
            discardedEggs = 0,
            mortalityCount = mortality,
            grades = lines.Select(l => new { eggGradeId = l.GradeId, quantity = l.Quantity }).ToArray()
        });
        Assert.Equal(HttpStatusCode.Created, record.StatusCode);
        var id = (await record.Content.ReadFromJsonAsync<Created>())!.Id;
        var submit = await client.PostWithKeyAsync(
            $"/api/v1/daily-entries/{id}/submit", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
        return id;
    }

    private static Task<EntryDto> GetEntryAsync(HttpClient client, Guid id) =>
        client.GetFromJsonAsync<EntryDto>($"/api/v1/daily-entries/{id}")!;

    private static Task<HttpResponseMessage> AdjustAsync(HttpClient client, Guid id, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/daily-entries/{id}/adjust")
        { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return client.SendAsync(request);
    }

    private async Task ConfirmSaleAsync(
        HttpClient client, Guid accountId, Guid farmId, Guid gradeId, int quantity)
    {
        // #99: sales lines sell products — one per call keeps the helper simple.
        var productId = await factory.SeedProductAsync(accountId, farmId, gradeId);
        var customer = await client.PostWithKeyAsync("/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = $"Buyer {Guid.NewGuid():N}"[..20], phone = "555-0100" });
        var customerId = (await customer.Content.ReadFromJsonAsync<Created>())!.Id;
        var order = await client.PostWithKeyAsync("/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId, orderDate = Today });
        var orderId = (await order.Content.ReadFromJsonAsync<Created>())!.Id;
        var item = await client.PostWithKeyAsync($"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { productId, quantity, unitPriceMinorUnits = 100 });
        Assert.True(item.IsSuccessStatusCode);
        var confirm = await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
    }

    [Fact]
    public async Task Adjust_ReconcilesLots_GrowShrinkAddRemove_AndSnapshots()
    {
        var (client, accountId, farmId, flockId, grades) = await SetupAsync("Large", "Medium", "Small");
        var entryId = await RecordAndSubmitAsync(client, farmId, flockId, Today, 1000, 0,
            (grades["Large"], 600), (grades["Medium"], 300));

        // Large shrinks, Medium is removed, Small is new.
        var current = await GetEntryAsync(client, entryId);
        var adjust = await AdjustAsync(client, entryId, new
        {
            version = current.Version,
            totalEggs = 900,
            crackedEggs = 0,
            dirtyEggs = 0,
            discardedEggs = 0,
            mortalityCount = 0,
            reason = "recount after grading error",
            grades = new[]
            {
                new { eggGradeId = grades["Large"], quantity = 500 },
                new { eggGradeId = grades["Small"], quantity = 200 },
            }
        });
        Assert.Equal(HttpStatusCode.OK, adjust.StatusCode);
        var result = await adjust.Content.ReadFromJsonAsync<AdjustDto>();
        Assert.Equal("ManagerAdjusted", result!.Status);

        var lots = await factory.WithTenantScopeAsync(accountId, db =>
            db.EggLots.Where(l => l.FlockId == flockId && l.ProductionDate == Today).ToListAsync());
        Assert.Equal(3, lots.Count);
        Assert.Equal(500, lots.Single(l => l.EggGradeId == grades["Large"]).QuantityProduced);
        Assert.Equal(0, lots.Single(l => l.EggGradeId == grades["Medium"]).QuantityProduced);
        Assert.Equal(200, lots.Single(l => l.EggGradeId == grades["Small"]).QuantityProduced);
        Assert.All(lots, l => Assert.Equal(l.QuantityProduced, l.QuantityAvailable));

        var after = await GetEntryAsync(client, entryId);
        Assert.Equal("recount after grading error", after.AdjustReason);
        Assert.Equal(result.Version, after.Version);
        // The audit snapshot carries the replaced values.
        Assert.Equal(1000, after.AdjustedFrom!.Value.GetProperty("totalEggs").GetInt32());
    }

    [Fact]
    public async Task Adjust_ShrinkBelowSold_Is422_AndNothingChanges()
    {
        var (client, accountId, farmId, flockId, grades) = await SetupAsync("Large");
        var entryId = await RecordAndSubmitAsync(client, farmId, flockId, Today, 600, 0,
            (grades["Large"], 600));
        await ConfirmSaleAsync(client, accountId, farmId, grades["Large"], 550);

        var current = await GetEntryAsync(client, entryId);
        var adjust = await AdjustAsync(client, entryId, new
        {
            version = current.Version,
            totalEggs = 500,
            crackedEggs = 0,
            dirtyEggs = 0,
            discardedEggs = 0,
            mortalityCount = 0,
            reason = "shrink below sold",
            grades = new[] { new { eggGradeId = grades["Large"], quantity = 500 } }
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, adjust.StatusCode);
        Assert.Contains("Large", await adjust.Content.ReadAsStringAsync());

        // Transaction rolled back: entry untouched, lot untouched.
        var after = await GetEntryAsync(client, entryId);
        Assert.Equal("Submitted", after.Status);
        Assert.Equal(current.Version, after.Version);
        Assert.Equal(600, after.TotalEggs);
        var lot = await factory.WithTenantScopeAsync(accountId, db =>
            db.EggLots.SingleAsync(l => l.FlockId == flockId && l.ProductionDate == Today));
        Assert.Equal(600, lot.QuantityProduced);
        Assert.Equal(50, lot.QuantityAvailable);
    }

    [Fact]
    public async Task Adjust_MortalityChanges_AppendCompensatingMovements()
    {
        var (client, accountId, farmId, flockId, grades) = await SetupAsync("Large");
        var entryId = await RecordAndSubmitAsync(client, farmId, flockId, Today, 100, 2,
            (grades["Large"], 90));

        async Task<int> CurrentBirdsAsync() =>
            (await client.GetFromJsonAsync<FlockDto>($"/api/v1/flocks/{flockId}"))!.CurrentBirds;
        Assert.Equal(98, await CurrentBirdsAsync());

        // 2 → 5: +3 Mortality row.
        var v1 = (await GetEntryAsync(client, entryId)).Version;
        var up = await AdjustAsync(client, entryId, new
        {
            version = v1, totalEggs = 100, crackedEggs = 0, dirtyEggs = 0, discardedEggs = 0,
            mortalityCount = 5, reason = "missed three dead birds"
        });
        Assert.Equal(HttpStatusCode.OK, up.StatusCode);
        Assert.Equal(95, await CurrentBirdsAsync());

        // 5 → 2: −3 Adjustment row (adds birds back). Original rows untouched.
        var v2 = (await GetEntryAsync(client, entryId)).Version;
        var down = await AdjustAsync(client, entryId, new
        {
            version = v2, totalEggs = 100, crackedEggs = 0, dirtyEggs = 0, discardedEggs = 0,
            mortalityCount = 2, reason = "they were only stunned"
        });
        Assert.Equal(HttpStatusCode.OK, down.StatusCode);
        Assert.Equal(98, await CurrentBirdsAsync());

        var movements = await factory.WithTenantScopeAsync(accountId, db =>
            db.BirdMovements.Where(m => m.DailyEntryId != null).OrderBy(m => m.Quantity).ToListAsync());
        Assert.Equal(3, movements.Count); // submit's +2, adjust's +3, adjust's −3
        Assert.Contains(movements, m => m.Quantity == 3 && m.Type.ToString() == "Mortality");
        Assert.Contains(movements, m => m.Quantity == -3 && m.Type.ToString() == "Adjustment");
    }

    [Fact]
    public async Task Void_Unsold_EmptiesLots_ReversesMortality_PreservesEntry()
    {
        var (client, accountId, farmId, flockId, grades) = await SetupAsync("Large");
        var entryId = await RecordAndSubmitAsync(client, farmId, flockId, Today, 100, 2,
            (grades["Large"], 90));

        var current = await GetEntryAsync(client, entryId);
        var voidResponse = await client.PostWithKeyAsync(
            $"/api/v1/daily-entries/{entryId}/void", Guid.NewGuid().ToString(),
            new { version = current.Version, reason = "entered under the wrong flock" });
        Assert.Equal(HttpStatusCode.OK, voidResponse.StatusCode);

        var after = await GetEntryAsync(client, entryId);
        Assert.Equal("Voided", after.Status);
        Assert.Equal("entered under the wrong flock", after.VoidReason);

        var lot = await factory.WithTenantScopeAsync(accountId, db =>
            db.EggLots.SingleAsync(l => l.FlockId == flockId && l.ProductionDate == Today));
        Assert.Equal(0, lot.QuantityProduced);
        Assert.Equal(0, lot.QuantityAvailable);

        var flock = await client.GetFromJsonAsync<FlockDto>($"/api/v1/flocks/{flockId}");
        Assert.Equal(100, flock!.CurrentBirds); // the 2 deaths reversed

        // Nothing further on a voided entry.
        var again = await AdjustAsync(client, entryId, new
        {
            version = after.Version, totalEggs = 50, crackedEggs = 0, dirtyEggs = 0,
            discardedEggs = 0, mortalityCount = 0, reason = "no"
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
    }

    // #82 — voiding vacates the natural key: the same house/flock/day can be
    // recorded again as a fresh entry, any number of times; a LIVE entry on
    // the key still blocks re-recording (partial unique index).
    [Fact]
    public async Task Void_VacatesDay_SameKeyCanBeReRecorded()
    {
        var (client, accountId, farmId, flockId, grades) = await SetupAsync("Large");
        var houseId = Guid.NewGuid();

        async Task<(HttpStatusCode Status, Guid Id)> RecordAsync(int total, int graded)
        {
            var response = await client.PostWithKeyAsync(
                "/api/v1/daily-entries", Guid.NewGuid().ToString(), new
                {
                    farmId,
                    houseId,
                    flockId,
                    date = Today,
                    totalEggs = total,
                    crackedEggs = 0,
                    dirtyEggs = 0,
                    discardedEggs = 0,
                    mortalityCount = 1,
                    grades = new[] { new { eggGradeId = grades["Large"], quantity = graded } }
                });
            var id = response.StatusCode == HttpStatusCode.Created
                ? (await response.Content.ReadFromJsonAsync<Created>())!.Id
                : Guid.Empty;
            return (response.StatusCode, id);
        }

        async Task VoidAsync(Guid id, string reason)
        {
            var current = await GetEntryAsync(client, id);
            var response = await client.PostWithKeyAsync(
                $"/api/v1/daily-entries/{id}/void", Guid.NewGuid().ToString(),
                new { version = current.Version, reason });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var (_, firstId) = await RecordAsync(100, 90);
        var submit = await client.PostWithKeyAsync(
            $"/api/v1/daily-entries/{firstId}/submit", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);

        // A live (submitted) entry still owns the key.
        var (blocked, _) = await RecordAsync(50, 40);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked);

        await VoidAsync(firstId, "wrong numbers, starting over");

        // Voided → the key is vacant: a fresh entry, not an edit of the old one.
        var (reStatus, secondId) = await RecordAsync(60, 50);
        Assert.Equal(HttpStatusCode.Created, reStatus);
        Assert.NotEqual(firstId, secondId);

        var first = await GetEntryAsync(client, firstId);
        Assert.Equal("Voided", first.Status);
        Assert.Equal(100, first.TotalEggs); // the voided entry keeps its history

        var second = await GetEntryAsync(client, secondId);
        Assert.Equal("Draft", second.Status);
        Assert.Equal(60, second.TotalEggs);

        // The full lifecycle works on the replacement, and voiding it too
        // leaves TWO voided rows on the key — then a third entry still fits.
        var submit2 = await client.PostWithKeyAsync(
            $"/api/v1/daily-entries/{secondId}/submit", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, submit2.StatusCode);
        await VoidAsync(secondId, "still wrong");

        var (thirdStatus, thirdId) = await RecordAsync(70, 10);
        Assert.Equal(HttpStatusCode.Created, thirdStatus);

        // History lists every row for the day, voided and live alike.
        var list = await client.GetFromJsonAsync<List<EntryDto>>(
            $"/api/v1/daily-entries?flockId={flockId}&from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        Assert.Equal(3, list!.Count);
        Assert.Equal(2, list.Count(e => e.Status == "Voided"));
        Assert.Contains(list, e => e.Id == thirdId && e.Status == "Draft");

        // Bird ledger net: two submits each recorded 1 death, both voids
        // reversed them; the draft's death isn't posted until submit → back to
        // the seeded 100.
        var flock = await client.GetFromJsonAsync<FlockDto>($"/api/v1/flocks/{flockId}");
        Assert.Equal(100, flock!.CurrentBirds);
    }

    [Fact]
    public async Task Void_WithSoldStock_Is422()
    {
        var (client, accountId, farmId, flockId, grades) = await SetupAsync("Large");
        var entryId = await RecordAndSubmitAsync(client, farmId, flockId, Today, 600, 0,
            (grades["Large"], 600));
        await ConfirmSaleAsync(client, accountId, farmId, grades["Large"], 10);

        var current = await GetEntryAsync(client, entryId);
        var voidResponse = await client.PostWithKeyAsync(
            $"/api/v1/daily-entries/{entryId}/void", Guid.NewGuid().ToString(),
            new { version = current.Version, reason = "too late" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, voidResponse.StatusCode);
        Assert.Contains("sold", await voidResponse.Content.ReadAsStringAsync());

        Assert.Equal("Submitted", (await GetEntryAsync(client, entryId)).Status);
    }

    // AGENTS.md race rule: same base version, exactly one wins, Version delta 1.
    [Fact]
    public async Task ParallelAdjusts_SameBaseVersion_ExactlyOneWins()
    {
        var (client, accountId, farmId, flockId, grades) = await SetupAsync("Large");
        var entryId = await RecordAndSubmitAsync(client, farmId, flockId, Today, 100, 0,
            (grades["Large"], 90));
        var baseVersion = (await GetEntryAsync(client, entryId)).Version;

        object Body(int total, string reason) => new
        {
            version = baseVersion, totalEggs = total, crackedEggs = 0, dirtyEggs = 0,
            discardedEggs = 0, mortalityCount = 0, reason,
            grades = new[] { new { eggGradeId = grades["Large"], quantity = total - 10 } }
        };
        var responses = await Task.WhenAll(
            AdjustAsync(client, entryId, Body(80, "first")),
            AdjustAsync(client, entryId, Body(60, "second")));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        var after = await GetEntryAsync(client, entryId);
        Assert.Equal(baseVersion + 1, after.Version);
        // Whole-payload consistency: totals and the lot match the same winner.
        var lot = await factory.WithTenantScopeAsync(accountId, db =>
            db.EggLots.SingleAsync(l => l.FlockId == flockId && l.ProductionDate == Today));
        Assert.True(
            (after.TotalEggs == 80 && lot.QuantityProduced == 70)
            || (after.TotalEggs == 60 && lot.QuantityProduced == 50),
            $"blended write: entry {after.TotalEggs}, lot {lot.QuantityProduced}");
    }

    // Void is a new aggregate mutation — AGENTS.md requires its own race
    // test. Adjust vs void on the same base: exactly one wins; the loser's
    // side effects (lot changes, movements) must not exist.
    [Fact]
    public async Task ParallelAdjustAndVoid_SameBaseVersion_ExactlyOneWins()
    {
        var (client, accountId, farmId, flockId, grades) = await SetupAsync("Large");
        var entryId = await RecordAndSubmitAsync(client, farmId, flockId, Today, 100, 2,
            (grades["Large"], 90));
        var baseVersion = (await GetEntryAsync(client, entryId)).Version;

        var voidRequest = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/daily-entries/{entryId}/void")
        { Content = JsonContent.Create(new { version = baseVersion, reason = "void race" }) };
        voidRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var responses = await Task.WhenAll(
            AdjustAsync(client, entryId, new
            {
                version = baseVersion, totalEggs = 80, crackedEggs = 0, dirtyEggs = 0,
                discardedEggs = 0, mortalityCount = 2, reason = "adjust race",
                grades = new[] { new { eggGradeId = grades["Large"], quantity = 70 } }
            }),
            client.SendAsync(voidRequest));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        var after = await GetEntryAsync(client, entryId);
        Assert.Equal(baseVersion + 1, after.Version);
        var lot = await factory.WithTenantScopeAsync(accountId, db =>
            db.EggLots.SingleAsync(l => l.DailyEntryId == entryId));
        Assert.True(
            (after.Status == "ManagerAdjusted" && lot.QuantityProduced == 70)
            || (after.Status == "Voided" && lot.QuantityProduced == 0),
            $"blended outcome: {after.Status} / lot {lot.QuantityProduced}");
    }

    [Fact]
    public async Task Adjust_Guards()
    {
        var (client, _, farmId, flockId, grades) = await SetupAsync("Large");

        // Draft entries aren't adjustable or voidable — they're editable.
        var record = await client.PostWithKeyAsync("/api/v1/daily-entries", Guid.NewGuid().ToString(), new
        {
            farmId, houseId = Guid.NewGuid(), flockId, date = Today,
            totalEggs = 100, crackedEggs = 0, dirtyEggs = 0, discardedEggs = 0, mortalityCount = 0,
            grades = new[] { new { eggGradeId = grades["Large"], quantity = 50 } }
        });
        var draftId = (await record.Content.ReadFromJsonAsync<Created>())!.Id;
        var draft = await GetEntryAsync(client, draftId);
        var adjustDraft = await AdjustAsync(client, draftId, new
        {
            version = draft.Version, totalEggs = 90, crackedEggs = 0, dirtyEggs = 0,
            discardedEggs = 0, mortalityCount = 0, reason = "nope"
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, adjustDraft.StatusCode);
        var voidDraft = await client.PostWithKeyAsync(
            $"/api/v1/daily-entries/{draftId}/void", Guid.NewGuid().ToString(),
            new { version = draft.Version, reason = "nope" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, voidDraft.StatusCode);

        // Stale base version → deterministic 409.
        var entryId = await RecordAndSubmitAsync(client, farmId, flockId, Today.AddDays(-1), 100, 0,
            (grades["Large"], 50));
        var version = (await GetEntryAsync(client, entryId)).Version;
        var first = await AdjustAsync(client, entryId, new
        {
            version, totalEggs = 90, crackedEggs = 0, dirtyEggs = 0, discardedEggs = 0,
            mortalityCount = 0, reason = "first"
        });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var stale = await AdjustAsync(client, entryId, new
        {
            version, totalEggs = 80, crackedEggs = 0, dirtyEggs = 0, discardedEggs = 0,
            mortalityCount = 0, reason = "stale"
        });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        // Blank reason → 400 before any state is touched.
        var blank = await AdjustAsync(client, entryId, new
        {
            version = version + 1, totalEggs = 90, crackedEggs = 0, dirtyEggs = 0,
            discardedEggs = 0, mortalityCount = 0, reason = "   "
        });
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
    }

    // The live-verify bug that forced the DailyEntryId link: two houses mean
    // two entries for the same flock and day, so (flock, date) is NOT an
    // entry's lot surface — adjusting one entry must not touch its sibling's
    // lots, and voiding must not empty them.
    [Fact]
    public async Task Adjust_SiblingEntry_SameFlockAndDate_IsUntouched()
    {
        var (client, accountId, farmId, flockId, grades) = await SetupAsync("Large");
        var entryA = await RecordAndSubmitAsync(client, farmId, flockId, Today, 100, 0,
            (grades["Large"], 90));
        var entryB = await RecordAndSubmitAsync(client, farmId, flockId, Today, 200, 0,
            (grades["Large"], 150)); // different house — RecordAndSubmit invents one per call

        var current = await GetEntryAsync(client, entryA);
        var adjust = await AdjustAsync(client, entryA, new
        {
            version = current.Version, totalEggs = 80, crackedEggs = 0, dirtyEggs = 0,
            discardedEggs = 0, mortalityCount = 0, reason = "shrink A only",
            grades = new[] { new { eggGradeId = grades["Large"], quantity = 70 } }
        });
        Assert.Equal(HttpStatusCode.OK, adjust.StatusCode);

        var lots = await factory.WithTenantScopeAsync(accountId, db =>
            db.EggLots.Where(l => l.FlockId == flockId && l.ProductionDate == Today).ToListAsync());
        Assert.Equal(70, lots.Single(l => l.DailyEntryId == entryA).QuantityProduced);
        Assert.Equal(150, lots.Single(l => l.DailyEntryId == entryB).QuantityProduced);
    }

    [Fact]
    public async Task LockSweep_LocksOldSubmittedEntries_LeavesRecent_AdjustStillWorks()
    {
        var (client, _, farmId, flockId, grades) = await SetupAsync("Large");
        var oldEntry = await RecordAndSubmitAsync(
            client, farmId, flockId, Today.AddDays(-(DailyEntryLockSweep.LockAfterDays + 1)),
            100, 0, (grades["Large"], 50));
        var recentEntry = await RecordAndSubmitAsync(
            client, farmId, flockId, Today, 100, 0, (grades["Large"], 50));

        await factory.Services.GetRequiredService<DailyEntryLockSweep>()
            .RunAsync(CancellationToken.None);

        var locked = await GetEntryAsync(client, oldEntry);
        Assert.Equal("Locked", locked.Status);
        Assert.NotNull(locked.LockedAtUtc);
        Assert.Equal("Submitted", (await GetEntryAsync(client, recentEntry)).Status);

        // Locked is not read-only for admins: adjust lands as ManagerAdjusted.
        var adjust = await AdjustAsync(client, oldEntry, new
        {
            version = locked.Version, totalEggs = 90, crackedEggs = 0, dirtyEggs = 0,
            discardedEggs = 0, mortalityCount = 0, reason = "late correction",
            grades = new[] { new { eggGradeId = grades["Large"], quantity = 40 } }
        });
        Assert.Equal(HttpStatusCode.OK, adjust.StatusCode);
        Assert.Equal("ManagerAdjusted", (await GetEntryAsync(client, oldEntry)).Status);
    }
}
