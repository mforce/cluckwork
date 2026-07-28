namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Net.Http.Headers;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;

// #178 — the baseline startup seed (Seed:Enabled=true, no Seed:Demo) writes none
// of the currency-binding row types (§4.6), so a freshly seeded farm must boot
// with its currency still editable. Nothing else pins this: if a priced product
// or a default-cost item ever sneaks from DemoDataSeeder into DatabaseSeeder,
// every deployment ships currency-locked from first boot.
//
// Own factory (own container) rather than the shared IntegrationCollection:
// both seeders write to the fixed SeedDefaults.AccountId, and DemoSeedTests
// seeds priced products + sales orders into that account on the shared
// container — the flag there depends on test order.
public sealed class BaselineSeedFactory : CluckworkWebApplicationFactory
{
    // Runtime-generated per run — never a hardcoded credential.
    public string AdminEmail { get; } = $"seed-{Guid.NewGuid():N}@test.local";
    public string AdminPassword { get; } = $"Aa1!{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Seed:Enabled", "true");
        // Explicit, not just the SeedOptions default: a stray Seed__Demo=true in
        // the environment would seed demo data and lock the currency (§4.6),
        // failing this test for the wrong reason. Tests must be hermetic.
        builder.UseSetting("Seed:Demo", "false");
        builder.UseSetting("Seed:AdminEmail", AdminEmail);
        builder.UseSetting("Seed:AdminPassword", AdminPassword);
    }
}

public sealed class BaselineSeedCurrencyTests(BaselineSeedFactory factory)
    : IClassFixture<BaselineSeedFactory>
{
    private sealed record TokenDto(string AccessToken);
    private sealed record SettingsDto(bool CanChangeCurrency);

    [Fact]
    public async Task FreshBaselineSeed_LeavesCurrencyEditable()
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = factory.AdminEmail, password = factory.AdminPassword });
        // The seeder is best-effort (logs + skips on failure) — surface the
        // response body so a skipped seed doesn't read as a bare 400.
        var loginBody = await login.Content.ReadAsStringAsync();
        Assert.True(login.StatusCode == HttpStatusCode.OK,
            $"Seeded-admin login failed — did the baseline seed skip? {(int)login.StatusCode}: {loginBody}");
        var token = (await login.Content.ReadFromJsonAsync<TokenDto>())!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var settings = await client.GetFromJsonAsync<SettingsDto>("/api/v1/account/settings");

        Assert.True(settings!.CanChangeCurrency,
            "The baseline seed wrote a currency-binding row (§4.6). A farm that has " +
            "recorded nothing must be able to choose its currency (#178).");
    }
}
