namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Text.Json;
using Cluckwork.Api.Endpoints.Auth;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

// #145 — the refresh token is delivered only as an HttpOnly cookie; the body
// carries just the access token. CSRF posture: refresh/logout require the custom
// header. These assert the wire contract.
[Collection(IntegrationCollection.Name)]
public sealed class AuthCookieContractTests(CluckworkWebApplicationFactory factory)
{
    private static readonly WebApplicationFactoryClientOptions Cookieless =
        new() { HandleCookies = false };

    private async Task<(HttpResponseMessage Response, string SetCookie)> LoginRawAsync()
    {
        var email = $"c-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        var response = await factory.CreateClient(Cookieless).PostAsJsonAsync(
            "/api/v1/auth/login", new { email, password = TestHarness.Password });
        response.EnsureSuccessStatusCode();
        var setCookie = response.Headers.TryGetValues("Set-Cookie", out var v)
            ? v.First(c => c.StartsWith(AuthCookies.RefreshCookieName + "=", StringComparison.Ordinal))
            : "";
        return (response, setCookie);
    }

    [Fact]
    public async Task Login_sets_httponly_strict_pathscoped_cookie_and_omits_refresh_from_body()
    {
        var (response, setCookie) = await LoginRawAsync();

        // Cookie attributes (case-insensitive per the Set-Cookie grammar).
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/api/v1/auth", setCookie, StringComparison.OrdinalIgnoreCase);

        // Body must carry the access token but NOT the refresh token.
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("accessToken", out _));
        Assert.False(body.TryGetProperty("refreshToken", out _),
            "the refresh token must never appear in the response body");
    }

    [Fact]
    public async Task Refresh_without_the_csrf_header_is_forbidden()
    {
        var tokens = await factory.LoginAsync(
            await SeedAsync());
        var response = await factory.CreateClient(Cookieless)
            .PostRefreshAsync(tokens.RefreshToken, csrf: false);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_without_a_cookie_is_unauthorized()
    {
        var response = await factory.CreateClient(Cookieless).PostRefreshAsync(refreshToken: null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_without_the_csrf_header_is_forbidden()
    {
        var email = await SeedAsync();
        var tokens = await factory.LoginAsync(email);
        var authed = factory.CreateAuthedClient(tokens.AccessToken);
        var response = await authed.PostLogoutAsync(
            Guid.NewGuid().ToString(), tokens.RefreshToken, csrf: false);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<string> SeedAsync()
    {
        var email = $"c-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        return email;
    }
}

// The Secure attribute only appears when the request is HTTPS; drive login
// through a trusted proxy presenting X-Forwarded-Proto: https (reusing the #144
// forwarded-headers setup) to observe it.
public sealed class AuthCookieSecureTests(SecurityProxyFactory factory)
    : IClassFixture<SecurityProxyFactory>
{
    [Fact]
    public async Task Refresh_cookie_is_marked_secure_over_forwarded_https()
    {
        var email = $"c-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
            AllowAutoRedirect = false,
        });
        client.DefaultRequestHeaders.Add("X-Test-Remote", SecurityProxyFactory.TrustedProxy);
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login", new { email, password = TestHarness.Password });
        response.EnsureSuccessStatusCode();

        var setCookie = response.Headers.GetValues("Set-Cookie")
            .First(c => c.StartsWith(AuthCookies.RefreshCookieName + "=", StringComparison.Ordinal));
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
    }
}
