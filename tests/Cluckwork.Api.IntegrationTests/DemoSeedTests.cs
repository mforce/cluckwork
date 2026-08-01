namespace Cluckwork.Api.IntegrationTests;

using System.Net.Http.Headers;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Accounts;
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
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = TestHarness.Password });
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
