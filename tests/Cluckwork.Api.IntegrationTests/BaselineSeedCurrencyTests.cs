using Cluckwork.Domain.Modules.Farm.Accounts;
using System.Net;
using System.Net.Http.Headers;
using Cluckwork.Api.IntegrationTests.Infrastructure;

namespace Cluckwork.Api.IntegrationTests;

// #178 — the #283 migration-baked default account writes none of the
// currency-binding row types (§4.6), so a freshly provisioned farm must boot
// with its currency still editable. Nothing else pins this: if a priced
// product or a default-cost item ever sneaks into the base InsertData, every
// deployment ships currency-locked from first boot.
//
// Its own database keeps the default account free of priced products and sales
// orders seeded by other tests.
public sealed class BaselineSeedCurrencyTests : IClassFixture<CluckworkWebApplicationFactory>
{
    private readonly CluckworkWebApplicationFactory _factory;

    public BaselineSeedCurrencyTests(CluckworkWebApplicationFactory factory) => _factory = factory;

    private sealed record TokenDto(string AccessToken);
    private sealed record SettingsDto(bool CanChangeCurrency);

    [Fact]
    public async Task FreshlyMigratedDefaultAccount_LeavesCurrencyEditable()
    {
        // No Seed:* config anywhere — the account/roles/grades come straight
        // from the #283 migration; only the login user is seeded here, direct
        // to the DB, standing in for a real `bootstrap-admin` run.
        var email = $"seed-{Guid.NewGuid():N}@test.local";
        await _factory.SeedUserAsync(SeedDefaults.AccountId, email, Roles.Owner);

        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { farmCode = TestHarness.DefaultFarmCode, email, password = TestHarness.Password });
        var loginBody = await login.Content.ReadAsStringAsync();
        Assert.True(login.StatusCode == HttpStatusCode.OK,
            $"Login against the migration-seeded default account failed: {(int)login.StatusCode}: {loginBody}");
        var token = (await login.Content.ReadFromJsonAsync<TokenDto>())!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var settings = await client.GetFromJsonAsync<SettingsDto>("/api/v1/account/settings");

        Assert.True(settings!.CanChangeCurrency,
            "The #283 base migration wrote a currency-binding row (§4.6). A farm that has " +
            "recorded nothing must be able to choose its currency (#178).");
    }
}
