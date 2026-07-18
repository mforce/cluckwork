namespace Cluckwork.Api.IntegrationTests;

using System.Net.Http.Headers;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

// #58 — Seed:Demo sample data: seeds once through the real domain path on a
// fresh catalog, no-ops on the second startup, and never runs when the flag
// is off (the default for every other test in this collection — their hosts
// have no Seed:* config at all).
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
            builder.UseSetting("Seed:Demo", "true");
        });

    [Fact]
    public async Task DemoSeed_PopulatesEveryScreen_AndIsIdempotent()
    {
        // Runtime-generated credentials — never a hardcoded secret.
        var email = $"demo-{Guid.NewGuid():N}@test.local";
        var password = $"Aa1!{Guid.NewGuid():N}";

        // First startup seeds; CreateClient forces host initialization.
        using var host = DemoHost(email, password);
        var client = host.CreateClient();
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

        // Second startup on the same database: guard makes it a no-op.
        using var second = DemoHost(email, password);
        var client2 = second.CreateClient();
        var login2 = await client2.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        login2.EnsureSuccessStatusCode();
        var token2 = (await login2.Content.ReadFromJsonAsync<TokenDto>())!.AccessToken;
        client2.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token2);
        var flocksAfter = await client2.GetFromJsonAsync<List<FlockDto>>("/api/v1/flocks?includeArchived=true");
        Assert.Equal(3, flocksAfter!.Count);
    }
}
