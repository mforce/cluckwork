namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

// #91 — core reports: production math (hen-days from the bird ledger, official
// entries only), money summaries, the role split, and range guards.
[Collection(IntegrationCollection.Name)]
public sealed class ReportsTests(CluckworkWebApplicationFactory factory)
{
    private sealed record Created(Guid Id);
    private sealed record DayRow(
        DateOnly Date, int TotalEggs, int Cracked, int Dirty, int Discarded,
        int Sellable, int FromCounts, int Deaths,
        int RecordedFlocks, int ExpectedFlocks,
        long HenDays, long RecordedHenDays, int RatedEggs, decimal? HenDayPct);
    private sealed record GradeRow(Guid EggGradeId, string Name, int Quantity);
    private sealed record ProductionDto(
        List<DayRow> Days, int TotalEggs, int TotalSellable, int TotalFromCounts,
        int TotalDeaths, long TotalHenDays, long TotalRecordedHenDays,
        int TotalRatedEggs, decimal? PeriodHenDayPct, List<GradeRow> GradeTotals);
    private sealed record SalesDto(
        int ConfirmedCount, long RevenueMinorUnits, long PaidMinorUnits,
        long OutstandingMinorUnits, int VoidedCount, string CurrencyCode, int CurrencyMinorUnit);
    private sealed record ProfitDto(
        long RevenueMinorUnits, long ExpensesMinorUnits, long ProfitMinorUnits,
        string CurrencyCode, int CurrencyMinorUnit);

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

    // #396 — Cracked and Dirty can now become stock, so the report has to say
    // how many eggs the day produced WITHOUT being hand-graded. Sellable keeps
    // its meaning (the hand-graded remainder, and the target Daily Entry counts
    // down to); FromCounts is the separate figure, so the two are never
    // conflated and the period totals stay addable.
    //
    // Only SNAPSHOT-BACKED conditions count. This fixture makes Cracked saleable
    // and Dirty NOT, so a rule that simply added both counters would report 9
    // where the farm can only sell 6 — inventing stock that was recorded as a
    // loss.
    [Fact]
    public async Task Production_CountsOnlySnapshotBackedConditionsAsFromCounts()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var farmId = Guid.NewGuid();
        var grades = await factory.SeedEggGradesAsync(accountId, farmId, "Large");
        var flockId = await factory.SeedFlockAsync(accountId, farmId);

        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.EggGrades.Add(Domain.Eggs.EggGrade.Create(
                Guid.NewGuid(), accountId, farmId, "Cracked", Domain.Eggs.EggGradeType.Quality,
                60, isSaleable: true, dailyEntryKind: Domain.Eggs.DailyEntryKind.Cracked));
            db.EggGrades.Add(Domain.Eggs.EggGrade.Create(
                Guid.NewGuid(), accountId, farmId, "Dirty", Domain.Eggs.EggGradeType.Quality,
                61, isSaleable: false, dailyEntryKind: Domain.Eggs.DailyEntryKind.Dirty));
            await db.SaveChangesAsync();
        });

        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        // 100 = 90 hand-graded + 6 cracked + 3 dirty + 1 discarded.
        var entryId = (await (await client.PostWithKeyAsync(
            "/api/v1/daily-entries", Guid.NewGuid().ToString(), new
            {
                farmId,
                houseId = Guid.NewGuid(),
                flockId,
                date = Today,
                totalEggs = 100,
                crackedEggs = 6,
                dirtyEggs = 3,
                discardedEggs = 1,
                mortalityCount = 0,
                grades = new[] { new { eggGradeId = grades["Large"], quantity = 90 } }
            })).Content.ReadFromJsonAsync<Created>())!.Id;
        await client.PostWithKeyAsync($"/api/v1/daily-entries/{entryId}/submit", Guid.NewGuid().ToString());

        var report = await client.GetFromJsonAsync<ProductionDto>(
            $"/api/v1/reports/production?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}");

        var day = Assert.Single(report!.Days);
        Assert.Equal(100, day.TotalEggs);
        Assert.Equal(90, day.Sellable);      // unchanged: the hand-graded remainder
        Assert.Equal(6, day.FromCounts);     // cracked only — dirty stayed a loss
        Assert.Equal(90, report.TotalSellable);
        Assert.Equal(6, report.TotalFromCounts);

        // #396 (codex review of #407): the "By grade" breakdown has to account
        // for the condition stock this same response just reported as produced.
        // Condition production creates an EggLot but NEVER a DailyEntryGrade row
        // — ConditionGradeGuard refuses a manual line naming a condition grade,
        // so one can never exist — and a breakdown built from DailyEntryGrades
        // alone therefore omitted every cracked egg while the header counted it.
        Assert.Equal(90, Assert.Single(report.GradeTotals, g => g.Name == "Large").Quantity);
        Assert.Equal(6, Assert.Single(report.GradeTotals, g => g.Name == "Cracked").Quantity);

        // Dirty was non-saleable, so it resolved to nothing and stayed a loss.
        // Its absence is the other half of the guarantee: folding the counters
        // in must not resurrect the ones the entry recorded as losses.
        Assert.DoesNotContain(report.GradeTotals, g => g.Name == "Dirty");

        // The whole point, stated as the arithmetic a reader of the screen does:
        // the breakdown sums to the hand-graded remainder PLUS the condition
        // stock, not to the remainder alone.
        Assert.Equal(
            report.TotalSellable + report.TotalFromCounts,
            report.GradeTotals.Sum(g => g.Quantity));
    }

    [Fact]
    public async Task Production_HenDays_OfficialEntriesOnly()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var farmId = Guid.NewGuid();
        var grades = await factory.SeedEggGradesAsync(accountId, farmId, "Large");
        // Seeded flock: 100 birds placed well before the range.
        var houseId = Guid.NewGuid();
        var flockId = await factory.SeedFlockAsync(accountId, farmId, houseId);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        async Task<Guid> RecordAsync(DateOnly date, int total, int mortality, bool submit)
        {
            // #394: submit requires exact reconciliation — grade the entire
            // sellable amount (total minus the 2 cracked + 1 dirty below) so a
            // submitted entry's stock actually matches its own report figures.
            var sellable = total - 2 - 1;
            var response = await client.PostWithKeyAsync("/api/v1/daily-entries", Guid.NewGuid().ToString(), new
            {
                farmId,
                houseId,
                flockId,
                date,
                totalEggs = total,
                crackedEggs = 2,
                dirtyEggs = 1,
                discardedEggs = 0,
                mortalityCount = mortality,
                grades = new[] { new { eggGradeId = grades["Large"], quantity = sellable } }
            });
            var id = (await response.Content.ReadFromJsonAsync<Created>())!.Id;
            if (submit)
                await client.PostWithKeyAsync($"/api/v1/daily-entries/{id}/submit", Guid.NewGuid().ToString());
            return id;
        }

        // Day -1: submitted (official, 2 deaths). Day 0: submitted + a sibling
        // DRAFT that must not count.
        await RecordAsync(Today.AddDays(-1), 80, 2, submit: true);
        await RecordAsync(Today, 90, 0, submit: true);
        await RecordAsync(Today, 500, 0, submit: false); // draft — excluded

        var report = await client.GetFromJsonAsync<ProductionDto>(
            $"/api/v1/reports/production?from={Today.AddDays(-1):yyyy-MM-dd}&to={Today:yyyy-MM-dd}");

        Assert.Equal(2, report!.Days.Count);
        var day1 = report.Days[0];
        Assert.Equal(80, day1.TotalEggs);
        Assert.Equal(77, day1.Sellable); // 80 − 2 − 1
        Assert.Equal(2, day1.Deaths);
        // Start-of-day convention (industry hen-day practice): the day's own
        // deaths do not shrink that day's denominator.
        Assert.Equal(100, day1.HenDays);
        Assert.Equal(80.0m, day1.HenDayPct);

        var day2 = report.Days[1];
        Assert.Equal(90, day2.TotalEggs); // the draft's 500 is invisible
        Assert.Equal(98, day2.HenDays);   // yesterday's 2 deaths bite today
        Assert.Equal(170, report.TotalEggs);
        Assert.Equal(198, report.TotalHenDays);
        // Period % = 170/198 — not the average of daily percentages. Every day
        // here is recorded, so recorded hen-days equal total hen-days and this
        // assertion would survive a revert of #780's denominator; the guard for
        // that is Production_RecordedFlocks_... below, not this one.
        Assert.Equal(85.9m, report.PeriodHenDayPct);
        // #394: each submitted day is now graded exactly to its own sellable
        // count (77 + 87), not the old arbitrary total-10 stand-in.
        Assert.Equal(77 + 87, report.GradeTotals.Single().Quantity);
    }

    // #780 — every figure on a production day defaults to 0, so a day nobody
    // recorded and a day that genuinely produced no eggs arrived identical to
    // every consumer. `EntryCount` is the only field that separates them, and
    // it counts OFFICIAL entries, so a day holding only a Draft is unrecorded
    // here even though the capture screen shows it as captured.
    [Fact]
    public async Task Production_RecordedFlocks_SeparatesAnUnrecordedDayFromAZeroEggDay()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var farmId = Guid.NewGuid();
        var grades = await factory.SeedEggGradesAsync(accountId, farmId, "Large");
        // The flock's own house, and the entries are filed from it: since #780 a
        // filing only answers for the house that owes the count.
        var houseId = Guid.NewGuid();
        var flockId = await factory.SeedFlockAsync(accountId, farmId, houseId);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        async Task RecordAsync(DateOnly date, int total, bool submit)
        {
            var response = await client.PostWithKeyAsync("/api/v1/daily-entries", Guid.NewGuid().ToString(), new
            {
                farmId,
                houseId,
                flockId,
                date,
                totalEggs = total,
                crackedEggs = 0,
                dirtyEggs = 0,
                discardedEggs = 0,
                mortalityCount = 0,
                grades = total == 0
                    ? Array.Empty<object>()
                    : [new { eggGradeId = grades["Large"], quantity = total }]
            });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var id = (await response.Content.ReadFromJsonAsync<Created>())!.Id;
            if (submit)
                Assert.Equal(HttpStatusCode.OK, (await client.PostWithKeyAsync(
                    $"/api/v1/daily-entries/{id}/submit", Guid.NewGuid().ToString())).StatusCode);
        }

        await RecordAsync(Today.AddDays(-4), 60, submit: true);
        await RecordAsync(Today.AddDays(-3), 0, submit: true);   // the flock laid nothing, and someone said so
        // Today-2: nobody recorded anything at all.
        await RecordAsync(Today.AddDays(-1), 90, submit: false); // a Draft is not an official entry

        var report = await client.GetFromJsonAsync<ProductionDto>(
            $"/api/v1/reports/production?from={Today.AddDays(-4):yyyy-MM-dd}&to={Today.AddDays(-1):yyyy-MM-dd}");

        Assert.Equal(4, report!.Days.Count);
        var (laid, zero, missing, draftOnly) = (report.Days[0], report.Days[1], report.Days[2], report.Days[3]);

        Assert.Equal((60, 1), (laid.TotalEggs, laid.RecordedFlocks));
        // The pair this issue exists for: identical in every other field.
        Assert.Equal(0, zero.TotalEggs);
        Assert.Equal(0, missing.TotalEggs);
        Assert.Equal(1, zero.RecordedFlocks);
        Assert.Equal(0, missing.RecordedFlocks);
        // And the Draft sits on the unrecorded side, with its 90 eggs invisible.
        Assert.Equal((0, 0), (draftOnly.TotalEggs, draftOnly.RecordedFlocks));

        // The lay rate follows the evidence, not the calendar. The house was
        // alive on all four days, so `HenDays` is 100 throughout and the Reports
        // column keeps the meaning the glossary gives it; what changes is the
        // denominator the RATE divides by.
        Assert.Equal((100L, 100L), (laid.HenDays, laid.RecordedHenDays));
        Assert.Equal(60m, laid.HenDayPct);
        // Recorded and genuinely zero: exposure counted, rate is a real 0%.
        Assert.Equal((100L, 100L), (zero.HenDays, zero.RecordedHenDays));
        Assert.Equal(0m, zero.HenDayPct);
        // Nobody reported: birds alive, but no exposure with evidence behind it,
        // so there is no rate at all. Printing 0.0% here was the conflation.
        Assert.Equal((100L, 0L), (missing.HenDays, missing.RecordedHenDays));
        Assert.Null(missing.HenDayPct);
        Assert.Null(draftOnly.HenDayPct);

        // Period: 60 eggs over the 200 hen-days that reported = 30%, not the
        // 15% that dividing by all 400 calendar hen-days would give.
        Assert.Equal(400L, report.TotalHenDays);
        Assert.Equal(200L, report.TotalRecordedHenDays);
        Assert.Equal(30m, report.PeriodHenDayPct);
    }

    // #780 — a day where SOME houses reported is not a day of low production
    // and not a day of no production; it is a day whose total is a floor. The
    // count of houses that owed a filing is what makes it visible, and the lay
    // rate must divide only by the houses that filed or a missing house reads
    // as a collapse in output.
    [Fact]
    public async Task Production_PartiallyRecordedDay_IsCountedAndRatedOnTheHousesThatReported()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var farmId = Guid.NewGuid();
        var grades = await factory.SeedEggGradesAsync(accountId, farmId, "Large");
        // Two flocks in two houses, 100 birds each, both placed well before the
        // window. Each files from its own house, which is what a filing has to
        // do to answer for that house (#780).
        var houseAId = Guid.NewGuid();
        var houseBId = Guid.NewGuid();
        var houseA = await factory.SeedFlockAsync(accountId, farmId, houseAId);
        var houseB = await factory.SeedFlockAsync(accountId, farmId, houseBId);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        async Task RecordAsync(Guid flockId, DateOnly date, int total)
        {
            var response = await client.PostWithKeyAsync("/api/v1/daily-entries", Guid.NewGuid().ToString(), new
            {
                farmId,
                houseId = flockId == houseA ? houseAId : houseBId,
                flockId,
                date,
                totalEggs = total,
                crackedEggs = 0,
                dirtyEggs = 0,
                discardedEggs = 0,
                mortalityCount = 0,
                grades = new[] { new { eggGradeId = grades["Large"], quantity = total } }
            });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var id = (await response.Content.ReadFromJsonAsync<Created>())!.Id;
            Assert.Equal(HttpStatusCode.OK, (await client.PostWithKeyAsync(
                $"/api/v1/daily-entries/{id}/submit", Guid.NewGuid().ToString())).StatusCode);
        }

        // Day -2: both houses report 80 each. Day -1: only house A does.
        await RecordAsync(houseA, Today.AddDays(-2), 80);
        await RecordAsync(houseB, Today.AddDays(-2), 80);
        await RecordAsync(houseA, Today.AddDays(-1), 80);

        var report = await client.GetFromJsonAsync<ProductionDto>(
            $"/api/v1/reports/production?from={Today.AddDays(-2):yyyy-MM-dd}&to={Today.AddDays(-1):yyyy-MM-dd}");

        var (full, partial) = (report!.Days[0], report.Days[1]);

        Assert.Equal((2, 2), (full.RecordedFlocks, full.ExpectedFlocks));
        // The whole point: one of two houses filed, and the row says so.
        Assert.Equal((1, 2), (partial.RecordedFlocks, partial.ExpectedFlocks));

        // Both houses were alive on both days, so the exposure is 200 either
        // way; only the reported half of it carries evidence on the second day.
        Assert.Equal((200L, 200L), (full.HenDays, full.RecordedHenDays));
        Assert.Equal((200L, 100L), (partial.HenDays, partial.RecordedHenDays));

        // 80% on both days. Dividing the partial day's 80 eggs by all 200
        // hen-days would report 40% and show a healthy farm halving its output
        // overnight, when what actually happened is that somebody did not file.
        Assert.Equal(80m, full.HenDayPct);
        Assert.Equal(80m, partial.HenDayPct);
        Assert.Equal(80m, report.PeriodHenDayPct);
    }

    // #780 — the lay rate's numerator must come from the same flocks as its
    // denominator. Depletion is routinely recorded after the fact, so a flock
    // can hold an official entry for a date the bird ledger later says it had
    // already ended: its eggs are in the day's total, its birds are in no
    // exposure. Dividing one by the other reported 160% on data the pre-#780
    // code rated at a correct 80%.
    [Fact]
    public async Task Production_BackdatedDepletion_LeavesItsEggsOutOfTheRateRatherThanOver100()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var farmId = Guid.NewGuid();
        var grades = await factory.SeedEggGradesAsync(accountId, farmId, "Large");
        var staying = await factory.SeedFlockAsync(accountId, farmId);
        var leaving = await factory.SeedFlockAsync(accountId, farmId);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        var day = Today.AddDays(-1);

        async Task RecordAsync(Guid flockId)
        {
            var house = await factory.WithTenantScopeAsync(accountId, async db =>
                (await db.Flocks.FirstAsync(f => f.Id == flockId)).HouseId);
            var response = await client.PostWithKeyAsync("/api/v1/daily-entries", Guid.NewGuid().ToString(), new
            {
                farmId, houseId = house, flockId, date = day,
                totalEggs = 80, crackedEggs = 0, dirtyEggs = 0, discardedEggs = 0, mortalityCount = 0,
                grades = new[] { new { eggGradeId = grades["Large"], quantity = 80 } }
            });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var id = (await response.Content.ReadFromJsonAsync<Created>())!.Id;
            Assert.Equal(HttpStatusCode.OK, (await client.PostWithKeyAsync(
                $"/api/v1/daily-entries/{id}/submit", Guid.NewGuid().ToString())).StatusCode);
        }

        await RecordAsync(staying);
        await RecordAsync(leaving);
        // The depletion lands afterwards and is backdated before the entry —
        // the write path refuses an entry against an already-depleted flock, so
        // this order is the only way in, and it is how a farm records it.
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var f = await db.Flocks.FirstAsync(x => x.Id == leaving);
            f.Deplete(day.AddDays(-1));
            await db.SaveChangesAsync();
        });

        var report = await client.GetFromJsonAsync<ProductionDto>(
            $"/api/v1/reports/production?from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}");
        var row = report!.Days.Single();

        // Both flocks' eggs stay in the day's total — the egg column may not
        // silently drop a filed row.
        Assert.Equal(160, row.TotalEggs);
        // Only the live flock is exposure, and only its eggs are rated.
        Assert.Equal((100L, 80), (row.RecordedHenDays, row.RatedEggs));
        Assert.Equal(80m, row.HenDayPct);
        Assert.Equal(80m, report.PeriodHenDayPct);
        Assert.True(row.HenDayPct <= 100m, "a lay rate over 100% means the numerator outran its own denominator");
    }

    // #780 — a filing counts as a filing whatever house id it carries. Matching
    // the entry's house against the flock's was tried and reverted: houses are
    // not aggregates yet (`RecordDailyEntryHandler` calls them "phantom ids
    // until Phase 2's House model" and declines to check them), every flock is
    // created with the same `SeedDefaults.HouseId`, so the rule could not fire
    // on real data while a client sending any other id turned a submitted day
    // into "no entry". This pins the revert so it is not re-introduced without
    // the write-path check it needs.
    [Fact]
    public async Task Production_EntryWithAnUnrelatedHouseId_StillCountsAsThatFlocksFiling()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var farmId = Guid.NewGuid();
        var grades = await factory.SeedEggGradesAsync(accountId, farmId, "Large");
        var flockId = await factory.SeedFlockAsync(accountId, farmId);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        var day = Today.AddDays(-1);

        var response = await client.PostWithKeyAsync("/api/v1/daily-entries", Guid.NewGuid().ToString(), new
        {
            farmId, houseId = Guid.NewGuid(), flockId, date = day,
            totalEggs = 40, crackedEggs = 0, dirtyEggs = 0, discardedEggs = 0, mortalityCount = 0,
            grades = new[] { new { eggGradeId = grades["Large"], quantity = 40 } }
        });
        var id = (await response.Content.ReadFromJsonAsync<Created>())!.Id;
        await client.PostWithKeyAsync($"/api/v1/daily-entries/{id}/submit", Guid.NewGuid().ToString());

        var report = await client.GetFromJsonAsync<ProductionDto>(
            $"/api/v1/reports/production?from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}");
        var row = report!.Days.Single();

        Assert.Equal((40, 1, 1), (row.TotalEggs, row.RecordedFlocks, row.ExpectedFlocks));
        Assert.Equal(40m, row.HenDayPct);
    }

    // #780 — a placement date corrected forward past entries that already exist
    // leaves a day whose filings all come from flocks the ledger says were not
    // yet placed. Deriving RecordedFlocks from the lifecycle walk made
    // `recorded <= expected` hold by construction, so that day collapsed to
    // (0, 0) and the strip drew real submitted eggs as "no flocks".
    [Fact]
    public async Task Production_PlacementCorrectedPastItsEntries_StillReportsThemAsFiled()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var farmId = Guid.NewGuid();
        var grades = await factory.SeedEggGradesAsync(accountId, farmId, "Large");
        var flockId = await factory.SeedFlockAsync(accountId, farmId);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        var day = Today.AddDays(-3);

        var response = await client.PostWithKeyAsync("/api/v1/daily-entries", Guid.NewGuid().ToString(), new
        {
            farmId, houseId = Guid.NewGuid(), flockId, date = day,
            totalEggs = 80, crackedEggs = 0, dirtyEggs = 0, discardedEggs = 0, mortalityCount = 0,
            grades = new[] { new { eggGradeId = grades["Large"], quantity = 80 } }
        });
        var id = (await response.Content.ReadFromJsonAsync<Created>())!.Id;
        await client.PostWithKeyAsync($"/api/v1/daily-entries/{id}/submit", Guid.NewGuid().ToString());

        // The typo correction: placement moves to AFTER the day that was filed.
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var f = await db.Flocks.FirstAsync(x => x.Id == flockId);
            f.Update(f.Name, f.Breed, Today.AddDays(-1), f.InitialCount);
            await db.SaveChangesAsync();
        });

        var report = await client.GetFromJsonAsync<ProductionDto>(
            $"/api/v1/reports/production?from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}");
        var row = report!.Days.Single();

        // The eggs were submitted and the day says so, however the ledger now
        // reads. No flock was live, so there is no exposure and no rate.
        Assert.Equal(80, row.TotalEggs);
        Assert.Equal((1, 0), (row.RecordedFlocks, row.ExpectedFlocks));
        Assert.Equal((0L, 0), (row.RecordedHenDays, row.RatedEggs));
        Assert.Null(row.HenDayPct);
    }

    // #780 — nothing on the write path bounds a mortality against the flock's
    // own count, so an over-removed flock has eggs and no birds. Admitting its
    // eggs to a numerator whose denominator excludes it reported 160% on a farm
    // laying 80%.
    [Fact]
    public async Task Production_OverRemovedFlock_LeavesItsEggsOutOfTheRate()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var farmId = Guid.NewGuid();
        var grades = await factory.SeedEggGradesAsync(accountId, farmId, "Large");
        var wiped = await factory.SeedFlockAsync(accountId, farmId);
        var healthy = await factory.SeedFlockAsync(accountId, farmId);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        async Task RecordAsync(Guid flockId, DateOnly date, int total, int mortality)
        {
            var response = await client.PostWithKeyAsync("/api/v1/daily-entries", Guid.NewGuid().ToString(), new
            {
                farmId, houseId = Guid.NewGuid(), flockId, date,
                totalEggs = total, crackedEggs = 0, dirtyEggs = 0, discardedEggs = 0,
                mortalityCount = mortality,
                grades = total == 0 ? Array.Empty<object>() : [new { eggGradeId = grades["Large"], quantity = total }]
            });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var id = (await response.Content.ReadFromJsonAsync<Created>())!.Id;
            Assert.Equal(HttpStatusCode.OK, (await client.PostWithKeyAsync(
                $"/api/v1/daily-entries/{id}/submit", Guid.NewGuid().ToString())).StatusCode);
        }

        // A mistyped mortality removes ten times the flock's 100 birds.
        await RecordAsync(wiped, Today.AddDays(-6), 0, 1000);
        await RecordAsync(wiped, Today.AddDays(-1), 80, 0);
        await RecordAsync(healthy, Today.AddDays(-1), 80, 0);

        var report = await client.GetFromJsonAsync<ProductionDto>(
            $"/api/v1/reports/production?from={Today.AddDays(-1):yyyy-MM-dd}&to={Today.AddDays(-1):yyyy-MM-dd}");
        var row = report!.Days.Single();

        Assert.Equal(160, row.TotalEggs);
        // Only the flock with birds is exposure, and only its eggs are rated.
        Assert.Equal((100L, 80), (row.RecordedHenDays, row.RatedEggs));
        Assert.Equal(80m, row.HenDayPct);
        Assert.True(row.HenDayPct <= 100m, "a lay rate over 100% means the numerator outran its own denominator");
        // The payload carries both halves, so the figure is reproducible.
        Assert.Equal(
            Math.Round(row.RatedEggs * 100m / row.RecordedHenDays, 1),
            row.HenDayPct);
    }

    // The lifecycle rule behind ExpectedFlocks — placed, not yet depleted or
    // archived — decides the partial state, the hatched bar, Peak and Avg. It
    // had no test: counting every loaded flock instead left all of these green.
    [Fact]
    public async Task Production_ExpectedFlocks_CountsOnlyFlocksLiveOnThatDay()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var farmId = Guid.NewGuid();
        await factory.SeedEggGradesAsync(accountId, farmId, "Large");
        var early = await factory.SeedFlockAsync(accountId, farmId);
        var late = await factory.SeedFlockAsync(accountId, farmId);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        // `late` is placed on the middle day of the window; `early` is depleted
        // on it. So each of the three days expects a different number.
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var l = await db.Flocks.FirstAsync(x => x.Id == late);
            l.Update(l.Name, l.Breed, Today.AddDays(-2), l.InitialCount);
            var e = await db.Flocks.FirstAsync(x => x.Id == early);
            e.Deplete(Today.AddDays(-2));
            await db.SaveChangesAsync();
        });

        var report = await client.GetFromJsonAsync<ProductionDto>(
            $"/api/v1/reports/production?from={Today.AddDays(-3):yyyy-MM-dd}&to={Today.AddDays(-1):yyyy-MM-dd}");

        // Before placement / after depletion, a flock owes nothing.
        Assert.Equal(1, report!.Days[0].ExpectedFlocks);  // early only
        Assert.Equal(2, report.Days[1].ExpectedFlocks);   // both, on the boundary day
        Assert.Equal(1, report.Days[2].ExpectedFlocks);   // late only
    }

    // Depletion writes no removal movement — the flock's contribution must
    // terminate at DepletedOn anyway (codex review of #92).
    [Fact]
    public async Task Production_DepletedFlock_StopsCountingAfterDepletionDay()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var farmId = Guid.NewGuid();
        // Flock A: 100 birds, stays active. Flock B: 100 birds, depleted 10
        // days ago with birds still on the books.
        await factory.SeedFlockAsync(accountId, farmId);
        var flockB = await factory.SeedFlockAsync(accountId, farmId);
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var f = await db.Flocks.SingleAsync(x => x.Id == flockB);
            f.Deplete(Today.AddDays(-10));
            await db.SaveChangesAsync();
        });
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        var report = await client.GetFromJsonAsync<ProductionDto>(
            $"/api/v1/reports/production?from={Today.AddDays(-11):yyyy-MM-dd}&to={Today.AddDays(-9):yyyy-MM-dd}");

        Assert.Equal(200, report!.Days[0].HenDays); // both flocks
        Assert.Equal(200, report.Days[1].HenDays);  // counts THROUGH its depletion day
        Assert.Equal(100, report.Days[2].HenDays);  // gone the day after
    }

    [Fact]
    public async Task SalesAndProfit_SummariesMatchLedger()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var farmId = Guid.NewGuid();
        var grades = await factory.SeedEggGradesAsync(accountId, farmId, "Large");
        var productId = await factory.SeedProductAsync(accountId, farmId, grades["Large"], "Large Eggs");
        var flockId = await factory.SeedFlockAsync(accountId, farmId);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        // Stock → confirmed order (40 × 100 = 4000) with a 1500 payment.
        var record = await client.PostWithKeyAsync("/api/v1/daily-entries", Guid.NewGuid().ToString(), new
        {
            farmId, houseId = Guid.NewGuid(), flockId, date = Today,
            totalEggs = 100, crackedEggs = 0, dirtyEggs = 0, discardedEggs = 0,
            mortalityCount = 0,
            grades = new[] { new { eggGradeId = grades["Large"], quantity = 100 } }
        });
        var entryId = (await record.Content.ReadFromJsonAsync<Created>())!.Id;
        await client.PostWithKeyAsync($"/api/v1/daily-entries/{entryId}/submit", Guid.NewGuid().ToString());
        var customer = await client.PostWithKeyAsync("/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = $"Buyer {Guid.NewGuid():N}"[..20], phone = "555-0100" });
        var customerId = (await customer.Content.ReadFromJsonAsync<Created>())!.Id;
        var order = await client.PostWithKeyAsync("/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId, orderDate = Today });
        var orderId = (await order.Content.ReadFromJsonAsync<Created>())!.Id;
        await client.PostWithKeyAsync($"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { productId, quantity = 40, unitPriceMinorUnits = 100 });
        await client.PostWithKeyAsync($"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString());
        await client.PostWithKeyAsync($"/api/v1/sales/{orderId}/payments", Guid.NewGuid().ToString(),
            new { paymentDate = Today, amountMinorUnits = 1500, method = "Cash" });

        // An expense in range: 900.
        var category = await client.PostWithKeyAsync("/api/v1/expense-categories", Guid.NewGuid().ToString(),
            new { name = "Feed" });
        var categoryId = (await category.Content.ReadFromJsonAsync<Created>())!.Id;
        await client.PostWithKeyAsync("/api/v1/expenses", Guid.NewGuid().ToString(), new
        {
            expenseCategoryId = categoryId, date = Today,
            description = "feed", amountMinorUnits = 900
        });

        var range = $"?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}";
        var sales = await client.GetFromJsonAsync<SalesDto>($"/api/v1/reports/sales{range}");
        Assert.Equal(1, sales!.ConfirmedCount);
        Assert.Equal(4000, sales.RevenueMinorUnits);
        Assert.Equal(1500, sales.PaidMinorUnits);
        Assert.Equal(2500, sales.OutstandingMinorUnits);

        var profit = await client.GetFromJsonAsync<ProfitDto>($"/api/v1/reports/profit{range}");
        Assert.Equal(4000, profit!.RevenueMinorUnits);
        Assert.Equal(900, profit.ExpensesMinorUnits);
        Assert.Equal(3100, profit.ProfitMinorUnits);
    }

    [Fact]
    public async Task RangeGuards_And_RoleSplit()
    {
        var adminEmail = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(adminEmail);
        var workerEmail = $"w-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, workerEmail, asAdmin: false);
        var admin = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(adminEmail));
        var worker = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(workerEmail));

        // from > to and >366 days → 400.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync(
            $"/api/v1/reports/production?from={Today:yyyy-MM-dd}&to={Today.AddDays(-1):yyyy-MM-dd}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync(
            $"/api/v1/reports/production?from={Today.AddDays(-400):yyyy-MM-dd}&to={Today:yyyy-MM-dd}")).StatusCode);

        // Workers read production, not money.
        Assert.Equal(HttpStatusCode.OK, (await worker.GetAsync("/api/v1/reports/production")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await worker.GetAsync("/api/v1/reports/sales")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await worker.GetAsync("/api/v1/reports/expenses")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await worker.GetAsync("/api/v1/reports/profit")).StatusCode);
    }

    // #311 — the exact boundary of the 366-day cap: the widest allowed span
    // succeeds unchanged, one day wider is rejected. RangeGuards_And_RoleSplit
    // above only proves a WAY-oversized range (400 days) 400s; these two lock
    // the boundary itself rather than somewhere comfortably past it.
    [Fact]
    public async Task Production_MaxAllowedRange_366Days_Succeeds()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        var from = Today.AddDays(-365); // 365 days before `to` — 366 calendar days inclusive.
        var response = await client.GetAsync(
            $"/api/v1/reports/production?from={from:yyyy-MM-dd}&to={Today:yyyy-MM-dd}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<ProductionDto>();
        Assert.Equal(366, report!.Days.Count);
    }

    [Fact]
    public async Task Production_OneDayOverMaxRange_Returns400()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        var from = Today.AddDays(-366); // one day past the 366-day cap.
        var response = await client.GetAsync(
            $"/api/v1/reports/production?from={from:yyyy-MM-dd}&to={Today:yyyy-MM-dd}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
