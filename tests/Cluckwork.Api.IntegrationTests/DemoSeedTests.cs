namespace Cluckwork.Api.IntegrationTests;

using System.Net.Http.Headers;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #58 — Demo sample data: seeds once through the real domain path on a fresh
// catalog, no-ops on the second call, and never runs on boot (the default for
// every other test in this collection — their hosts have no Seed:* config at
// all).
//
// #280/#284 — demo data is never boot-seeded and DemoDataSeeder no longer
// self-gates on any Seed:* flag (its only caller is the `seed --profile demo`
// command) — nothing invokes SeedAsync() automatically, so each host below
// calls it explicitly once host startup has been forced (migrations + the
// base DatabaseSeeder, which this depends on for the seeded account/roles/egg
// grades, still run on boot unchanged). The host still needs
// Seed:AdminEmail/Seed:AdminPassword set for THAT base seed to run at all.
[Collection(IntegrationCollection.Name)]
public sealed class DemoSeedTests(CluckworkWebApplicationFactory factory)
{
    private sealed record FlockDto(Guid Id, string Name, int InitialCount, long CurrentBirds, string Status);
    private sealed record StockDto(Guid EggGradeId, string GradeName, int Available, int Restricted);
    private sealed record OrderDto(Guid Id, string Status);
    private sealed record TokenDto(string AccessToken);

    private WebApplicationFactory<Program> DemoHost(string email, string password) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Seed:Enabled", "true");
            builder.UseSetting("Seed:AdminEmail", email);
            builder.UseSetting("Seed:AdminPassword", password);
        });

    [Fact]
    public async Task DemoSeed_PopulatesEveryScreen_AndIsIdempotent()
    {
        // Runtime-generated credentials — never a hardcoded secret.
        var email = $"demo-{Guid.NewGuid():N}@test.local";
        var password = $"Aa1!{Guid.NewGuid():N}";

        // First startup: CreateClient forces host initialization (migrations +
        // base DatabaseSeeder run on boot, unchanged). Demo data itself no
        // longer boot-seeds (#280) — call DemoDataSeeder directly.
        using var host = DemoHost(email, password);
        var client = host.CreateClient();
        using (var seedScope = host.Services.CreateScope())
        {
            var result = await seedScope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync();
            Assert.True(result.IsSuccess, result.Message);
        }
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
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

        // Second startup on the same database: the explicit SeedAsync call
        // below is a no-op — DemoDataSeeder's own empty-catalog guard fires.
        using var second = DemoHost(email, password);
        var client2 = second.CreateClient();
        using (var seedScope2 = second.Services.CreateScope())
        {
            var result2 = await seedScope2.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync();
            Assert.True(result2.IsSuccess, result2.Message);
            Assert.Equal(SeedStatus.AlreadySeeded, result2.Status);
        }
        var login2 = await client2.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        login2.EnsureSuccessStatusCode();
        var token2 = (await login2.Content.ReadFromJsonAsync<TokenDto>())!.AccessToken;
        client2.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token2);
        var flocksAfter = await client2.GetFromJsonAsync<List<FlockDto>>("/api/v1/flocks?includeArchived=true");
        Assert.Equal(3, flocksAfter!.Count);
    }

    // #284 review — a real "demo is OFF the boot path" regression test. The
    // idempotency test above always calls SeedAsync() explicitly before
    // checking flock counts, so it would still pass even if boot-time demo
    // seeding were reintroduced by mistake. This test instead boots a host
    // with ONLY the base seed configured, asserts zero demo rows exist yet
    // (proving boot alone never seeds demo), and only then seeds explicitly.
    [Fact]
    public async Task Boot_NeverAutoSeedsDemo_OnlyExplicitSeedAsyncDoes()
    {
        var email = $"demo-boot-{Guid.NewGuid():N}@test.local";
        var password = $"Aa1!{Guid.NewGuid():N}";

        using var host = DemoHost(email, password);
        // Forces host startup (migrations + base DatabaseSeeder) without
        // touching DemoDataSeeder at all.
        _ = host.Services;

        using (var preScope = host.Services.CreateScope())
        {
            var db = preScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var flockCountBeforeAnySeed = await db.Flocks
                .IgnoreQueryFilters()
                .CountAsync(f => f.AccountId == SeedDefaults.AccountId);
            Assert.Equal(0, flockCountBeforeAnySeed);
        }

        using (var seedScope = host.Services.CreateScope())
        {
            var result = await seedScope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync();
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(SeedStatus.Seeded, result.Status);
        }

        using (var postScope = host.Services.CreateScope())
        {
            var db = postScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var flockCountAfterSeed = await db.Flocks
                .IgnoreQueryFilters()
                .CountAsync(f => f.AccountId == SeedDefaults.AccountId);
            Assert.Equal(3, flockCountAfterSeed);
        }
    }
}
