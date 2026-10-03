using System.Net.Http.Json;
using Cluckwork.Api.Endpoints.Auth;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Infrastructure.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
        var days = factory.Services.GetRequiredService<IOptions<JwtOptions>>().Value.RefreshTokenDays;
        var email = $"cookie-life-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var cookieName = AuthCookies.RefreshCookieNameFor(accountId);
        var client = factory.CreateClient(TestHarness.Cookieless(factory));

        void AssertLifetime(HttpResponseMessage response, string step)
        {
            var cookie = response.Headers.GetValues("Set-Cookie").Select(header => SetCookie.Parse(header))
                .Single(c => c.Name.Value == cookieName);
            var expected = DateTimeOffset.UtcNow.AddDays(days);
            Assert.True(cookie.Expires is { } expires && (expires - expected).Duration() < TimeSpan.FromMinutes(2),
                $"{step}: expected the refresh cookie to expire about {expected:O}, got {cookie.Expires:O}");
        }

        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { farmCode = await factory.FarmCodeForAsync(email), email, password = TestHarness.Password });
        login.EnsureSuccessStatusCode();
        AssertLifetime(login, "login");
        var tokens = await TestHarness.ReadTokensAsync(login);

        var refresh = await client.PostRefreshAsync(tokens.RefreshToken, expectedAccount: accountId.ToString());
        refresh.EnsureSuccessStatusCode();
        AssertLifetime(refresh, "refresh");
        var rotated = await TestHarness.ReadTokensAsync(refresh);

        var authed = factory.CreateAuthedClient(rotated.AccessToken);
        var change = await authed.PostAsJsonAsync("/api/v1/auth/change-password",
            new { currentPassword = TestHarness.Password, newPassword = TestHarness.Password + "x" });
        change.EnsureSuccessStatusCode();
        AssertLifetime(change, "change-password");
    }
}
