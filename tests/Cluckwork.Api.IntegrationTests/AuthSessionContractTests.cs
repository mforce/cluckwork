using Cluckwork.Api.Modules.Access.Auth;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Serilog.Events;
using SetCookie = Microsoft.Net.Http.Headers.SetCookieHeaderValue;

namespace Cluckwork.Api.IntegrationTests;

// #857 — two things the auth endpoints decide themselves rather than through
// IIdentityProvider, which no suite pinned. The refresh cookie's lifetime comes
// from Jwt:RefreshTokenDays, and the unknown-farm and suspended-farm login
// branches log their own LoginFailed event before any credential is checked.
[Collection(SecurityEventLoggingCollection.Name)]
public sealed class AuthSessionContractTests(SecurityEventLoggingFactory factory)
{
    // Not the 30-day default, so a cookie written with a hardcoded or default
    // lifetime fails here rather than matching by coincidence.
    private const int RefreshTokenDays = 7;

    private WebApplicationFactory<Program> ShortLifetimeHost() =>
        factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Jwt:RefreshTokenDays", RefreshTokenDays.ToString(CultureInfo.InvariantCulture)));

    private static void AssertRefreshCookieLifetime(HttpResponseMessage response, string cookieName, string step)
    {
        var cookie = response.Headers.GetValues("Set-Cookie").Select(header => SetCookie.Parse(header))
            .Single(c => c.Name.Value == cookieName);
        var expected = DateTimeOffset.UtcNow.AddDays(RefreshTokenDays);
        Assert.True(cookie.Expires is { } expires && (expires - expected).Duration() < TimeSpan.FromMinutes(2),
            $"{step}: expected the refresh cookie to expire about {expected:O}, got {cookie.Expires:O}");
    }

    private static string? ScalarOf(LogEvent e, string name) =>
        e.Properties.TryGetValue(name, out var value) && value is ScalarValue scalar
            ? scalar.Value?.ToString()
            : null;

    private IReadOnlyList<LogEvent> LoginFailures() =>
        [.. factory.Sink.Events.Where(e => ScalarOf(e, "SecurityEvent") == SecurityEvents.LoginFailed)];

    private static async Task<string> TitleOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ProblemDetails>())!.Title!;

    [Fact]
    public async Task UnknownFarmAndSuspendedFarm_EachLogOneLoginFailed_WithTheUnknownUserShape()
    {
        factory.Sink.Events.Clear();
        var email = $"farm-fail-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var farmCode = await factory.FarmCodeForAsync(email);
        var client = factory.CreateClient();

        var unknownUser = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { farmCode, email = $"nobody-{Guid.NewGuid():N}@test.local", password = TestHarness.Password });
        var unknownFarm = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { farmCode = $"no-farm-{Guid.NewGuid():N}"[..20], email, password = TestHarness.Password });
        await factory.WithTenantScopeAsync(accountId, db => db.Accounts.Where(a => a.Id == accountId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(a => a.IsActive, false)));
        var suspended = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { farmCode, email, password = TestHarness.Password });

        Assert.Equal("Identity.InvalidCredentials", await TitleOf(unknownUser));
        Assert.Equal(AuthEndpoints.UnknownFarmCodeCode, await TitleOf(unknownFarm));
        Assert.Equal(AuthEndpoints.FarmSuspendedCode, await TitleOf(suspended));

        var events = LoginFailures();
        Assert.Equal(3, events.Count);
        var keys = events[0].Properties.Keys.OrderBy(k => k).ToList();
        foreach (var e in events)
        {
            Assert.Equal(keys, e.Properties.Keys.OrderBy(k => k).ToList());
            Assert.False(e.Properties.ContainsKey("UserId"));
            Assert.False(e.Properties.ContainsKey("Email"));
        }
    }

    [Fact]
    public async Task RefreshCookie_ExpiresAfterTheConfiguredLifetime_OnLoginRefreshAndPasswordChange()
    {
        using var host = ShortLifetimeHost();
        var email = $"cookie-life-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var cookieName = AuthCookies.RefreshCookieNameFor(accountId);
        var client = host.CreateClient(TestHarness.Cookieless(factory));

        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { farmCode = await factory.FarmCodeForAsync(email), email, password = TestHarness.Password });
        login.EnsureSuccessStatusCode();
        AssertRefreshCookieLifetime(login, cookieName, "login");
        var tokens = await TestHarness.ReadTokensAsync(login);

        var refresh = await client.PostRefreshAsync(tokens.RefreshToken, expectedAccount: accountId.ToString());
        refresh.EnsureSuccessStatusCode();
        AssertRefreshCookieLifetime(refresh, cookieName, "refresh");
        var rotated = await TestHarness.ReadTokensAsync(refresh);

        var authed = host.CreateClient(TestHarness.Cookieless(factory));
        authed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rotated.AccessToken);
        var change = await authed.PostAsJsonAsync("/api/v1/auth/change-password",
            new { currentPassword = TestHarness.Password, newPassword = TestHarness.Password + "x" });
        change.EnsureSuccessStatusCode();
        AssertRefreshCookieLifetime(change, cookieName, "change-password");
    }

    // A session still on the pre-#532 shared cookie name is upgraded to the
    // per-farm name on its next refresh, and the new cookie gets the same lifetime.
    [Fact]
    public async Task LegacyCookieUpgrade_WritesThePerFarmCookieForTheConfiguredLifetime()
    {
        using var host = ShortLifetimeHost();
        var email = $"legacy-life-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var client = host.CreateClient(TestHarness.Cookieless(factory));
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { farmCode = await factory.FarmCodeForAsync(email), email, password = TestHarness.Password });
        login.EnsureSuccessStatusCode();
        var legacyToken = (await TestHarness.ReadTokensAsync(login)).RefreshToken;

        var upgrade = await client.PostRefreshRawAsync(AuthCookies.LegacyRefreshCookieName + "=" + legacyToken);

        Assert.Equal(HttpStatusCode.OK, upgrade.StatusCode);
        AssertRefreshCookieLifetime(upgrade, AuthCookies.RefreshCookieNameFor(accountId), "legacy upgrade");
    }
}
