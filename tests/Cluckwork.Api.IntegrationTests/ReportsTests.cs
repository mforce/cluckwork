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
        int Sellable, int Deaths, long HenDays, decimal? HenDayPct);
    private sealed record GradeRow(Guid EggGradeId, string Name, int Quantity);
    private sealed record ProductionDto(
        List<DayRow> Days, int TotalEggs, int TotalSellable, int TotalDeaths,
        long TotalHenDays, decimal? PeriodHenDayPct, List<GradeRow> GradeTotals);
    private sealed record SalesDto(
        int ConfirmedCount, long RevenueMinorUnits, long PaidMinorUnits,
        long OutstandingMinorUnits, int VoidedCount, string CurrencyCode, int CurrencyMinorUnit);
    private sealed record ProfitDto(
        long RevenueMinorUnits, long ExpensesMinorUnits, long ProfitMinorUnits,
        string CurrencyCode, int CurrencyMinorUnit);

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

    [Fact]
    public async Task Production_HenDays_OfficialEntriesOnly()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var farmId = Guid.NewGuid();
        var grades = await factory.SeedEggGradesAsync(accountId, farmId, "Large");
        // Seeded flock: 100 birds placed well before the range.
        var flockId = await factory.SeedFlockAsync(accountId, farmId);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        async Task<Guid> RecordAsync(DateOnly date, int total, int mortality, bool submit)
        {
            var response = await client.PostWithKeyAsync("/api/v1/daily-entries", Guid.NewGuid().ToString(), new
            {
                farmId,
                houseId = Guid.NewGuid(),
                flockId,
                date,
                totalEggs = total,
                crackedEggs = 2,
                dirtyEggs = 1,
                discardedEggs = 0,
                mortalityCount = mortality,
                grades = new[] { new { eggGradeId = grades["Large"], quantity = total - 10 } }
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
        // Period % = 170/198 — not the average of daily percentages.
        Assert.Equal(85.9m, report.PeriodHenDayPct);
        Assert.Equal(70 + 80, report.GradeTotals.Single().Quantity);
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
