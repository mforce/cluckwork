namespace Cluckwork.Api.IntegrationTests;

using System.Net.Http.Headers;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Eggs;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #58 — Demo sample data: seeds once through the real domain path on a fresh
// catalog, no-ops on the second call, and never runs on boot.
//
// #280/#284 — demo data is never boot-seeded and DemoDataSeeder no longer
// self-gates on any Seed:* flag (its only caller is the `seed --profile demo`
// command) — nothing invokes SeedAsync() automatically, this calls it
// directly. #283 — the base account/Admin role/egg grades DemoDataSeeder's
// own preflight depends on are now #283 migration-baked static reference
// data (no Seed:* config, no runtime seeder); only the login user itself is
// seeded here, standing in for a real `bootstrap-admin` run.
//
// #500 — DO NOT ADD AN ATTRIBUTION ASSERTION TO THIS FILE. It would be flaky by
// construction, and the reason is not obvious from here: this class shares the
// IntegrationCollection container, and SeedAndFlockTests seeds its OWN Owner
// into the same SeedDefaults.AccountId. DemoDataSeeder.FindOwnerAsync picks the
// lowest Id among every Owner in the account, so WHICH Owner signs the demo
// fixture here depends on which sibling class ran first and on random GUID
// ordering — xUnit guarantees neither. Nothing in this file reads the author, so
// nothing is wrong today.
//
// The tests that DO assert attribution live in DemoSeedActorTests, each with its
// own factory and its own Postgres container, precisely to escape this.
[Collection(IntegrationCollection.Name)]
public sealed class DemoSeedTests(CluckworkWebApplicationFactory factory)
{
    private sealed record FlockDto(Guid Id, string Name, int InitialCount, long CurrentBirds, string Status);
    private sealed record StockDto(Guid EggGradeId, string GradeName, int Available, int Restricted);
    private sealed record OrderDto(Guid Id, string Status);
    private sealed record TokenDto(string AccessToken);

    [Fact]
    public async Task DemoSeed_PopulatesEveryScreen_AndIsIdempotent()
    {
        // Runtime-generated email — never a hardcoded secret.
        var email = $"demo-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(SeedDefaults.AccountId, email, Roles.Owner);

        var client = factory.CreateClient();
        using (var seedScope = factory.Services.CreateScope())
        {
            var result = await seedScope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync();
            Assert.True(result.IsSuccess, result.Message);
        }
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { farmCode = await factory.FarmCodeForAsync(email), email, password = TestHarness.Password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<TokenDto>())!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var flocks = await client.GetFromJsonAsync<List<FlockDto>>("/api/v1/flocks?includeArchived=true");
        Assert.Equal(3, flocks!.Count);
        Assert.Contains(flocks, f => f.Status == "Depleted");
        // Mortality movements + the cull actually landed in the ledger.
        Assert.Contains(flocks, f => f.Status == "Active" && f.CurrentBirds < f.InitialCount);
        Assert.Contains(flocks, f => f.Name.Contains("2025") && f.CurrentBirds == 20); // 450 - 430 cull

        var stock = await client.GetFromJsonAsync<List<StockDto>>("/api/v1/stock");
        Assert.True(stock!.Sum(s => s.Available) > 0, "demo stock is empty");

        var orders = await client.GetFromJsonAsync<List<OrderDto>>("/api/v1/sales");
        Assert.Contains(orders!, o => o.Status == "Confirmed");
        Assert.Contains(orders!, o => o.Status == "Draft");

        var entries = await client.GetFromJsonAsync<List<object>>("/api/v1/daily-entries");
        Assert.True(entries!.Count >= 14, $"expected a week of entries, got {entries.Count}");

        // #465 — the demo must OUT-PAGE the stock drill-down (50/page), so the
        // load-more pager and date filter are exercisable straight from a
        // demo-seeded farm. Per grade, because the panel filters by grade.
        var largeId = stock!.Single(s => s.GradeName == "Large").EggGradeId;
        var largeLots = await client.GetFromJsonAsync<List<object>>(
            $"/api/v1/stock/lots?gradeId={largeId}&limit=200");
        Assert.True(largeLots!.Count > 50,
            $"expected the Large grade to out-page the 50-lot stock page, got {largeLots.Count}");

        // A second SeedAsync call against the same database is a no-op —
        // DemoDataSeeder's own empty-catalog guard fires.
        using (var seedScope2 = factory.Services.CreateScope())
        {
            var result2 = await seedScope2.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync();
            Assert.True(result2.IsSuccess, result2.Message);
            Assert.Equal(SeedStatus.AlreadySeeded, result2.Status);
        }
        var client2 = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        var flocksAfter = await client2.GetFromJsonAsync<List<FlockDto>>("/api/v1/flocks?includeArchived=true");
        Assert.Equal(3, flocksAfter!.Count);
    }

    // The Dashboard's Today panel reads by the farm clock, and the demo seed
    // stamps House 1's draft "today". Dated by DateTime.UtcNow instead, the
    // draft lands on tomorrow's date for any farm west of UTC in the hours
    // after midnight UTC, and the panel shows no draft: every e2e smoke run
    // between 00:00 and 05:00 UTC failed on the demo farm (America/Chicago)
    // on 2026-09-17. This seeds a farm whose calendar date differs from UTC's
    // at the moment the test runs, whichever half of the UTC day that is, and
    // pins the draft to the farm's date.
    [Fact]
    public async Task DemoSeed_DatesTodaysDraft_ByTheFarmClock_NotUtc()
    {
        var utcNow = DateTime.UtcNow;
        var timeZoneId = utcNow.Hour < 12 ? "Etc/GMT+12" : "Pacific/Kiritimati";
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var farmToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utcNow, zone));
        var utcToday = DateOnly.FromDateTime(utcNow);
        Assert.NotEqual(utcToday, farmToday);

        var slug = $"clock-{Guid.NewGuid():N}"[..20];
        Guid accountId;
        using (var scope = factory.Services.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<AccountProvisioner>()
                .ProvisionAsync(name: "Clock Farm", slug: slug,
                    ownerEmail: $"{slug}@test.local", locale: "en-US", currencyCode: "USD",
                    timeZoneId: timeZoneId);
            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Description : string.Empty);
            accountId = result.Value.AccountId;
        }

        using (var seedScope = factory.Services.CreateScope())
        {
            var result = await seedScope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync(accountId);
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(SeedStatus.Seeded, result.Status);
        }

        using var readScope = factory.Services.CreateScope();
        var db = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var drafts = await db.DailyEntries.IgnoreQueryFilters()
            .Where(e => e.AccountId == accountId && e.Status == DailyEntryStatus.Draft)
            .Select(e => e.Date)
            .ToListAsync();
        var draftDate = Assert.Single(drafts);
        Assert.Equal(farmToday, draftDate);
    }

    // #284 review — a real "demo is OFF the boot path" regression test. The
    // idempotency test above always calls SeedAsync() explicitly before
    // checking flock counts, so it would still pass even if boot-time demo
    // seeding were reintroduced by mistake. This test instead boots a host
    // with ONLY the base seed configured, asserts zero demo rows exist yet
    // (proving boot alone never seeds demo), and only then seeds explicitly.
    // #283 review — this asserts against the SAME shared-container account
    // DemoSeed_PopulatesEveryScreen_AndIsIdempotent also seeds, so it must
    // run its own "before" check strictly before that test's SeedAsync call
    // lands. Both are [Collection(IntegrationCollection.Name)] on the same
    // container; xUnit runs [Fact]s within one class sequentially by default
    // (no [Collection]-level parallelism override here), which is what makes
    // the zero-count assertion below meaningful rather than a race.
    [Fact]
    public async Task Boot_NeverAutoSeedsDemo_OnlyExplicitSeedAsyncDoes()
    {
        using (var preScope = factory.Services.CreateScope())
        {
            var db = preScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var flockCountBeforeAnySeed = await db.Flocks
                .IgnoreQueryFilters()
                .CountAsync(f => f.AccountId == SeedDefaults.AccountId);
            Assert.Equal(0, flockCountBeforeAnySeed);
        }

        // #500 — the demo seed now signs every record with the account's Owner
        // and refuses to run without one, so provisioning it is part of the
        // "explicit seed" step this test is about. It stands in for a real
        // `bootstrap-admin` run, exactly as the sibling test's own Owner does.
        // The zero-flock assertion above still runs strictly first, so what the
        // test proves — boot alone seeds nothing — is unchanged.
        await factory.SeedUserAsync(SeedDefaults.AccountId, $"boot-{Guid.NewGuid():N}@test.local", Roles.Owner);

        using (var seedScope = factory.Services.CreateScope())
        {
            var result = await seedScope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync();
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(SeedStatus.Seeded, result.Status);
        }

        using (var postScope = factory.Services.CreateScope())
        {
            var db = postScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var flockCountAfterSeed = await db.Flocks
                .IgnoreQueryFilters()
                .CountAsync(f => f.AccountId == SeedDefaults.AccountId);
            Assert.Equal(3, flockCountAfterSeed);
        }
    }

}
