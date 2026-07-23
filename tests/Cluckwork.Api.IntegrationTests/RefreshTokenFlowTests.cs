namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

// tech spec §7.4: refresh tokens are durable and rotating. Refresh issues a new
// pair, the old token is single-use, reuse is rejected, and logout revokes.
// Since #145 the refresh token travels in an HttpOnly cookie, never the body.
[Collection(IntegrationCollection.Name)]
public sealed class RefreshTokenFlowTests(CluckworkWebApplicationFactory factory)
{
    private static readonly WebApplicationFactoryClientOptions Cookieless =
        new() { HandleCookies = false };

    private async Task<TokenPairDto> RefreshAsync(HttpClient client, string refreshToken)
    {
        var response = await client.PostRefreshAsync(refreshToken);
        response.EnsureSuccessStatusCode();
        return await TestHarness.ReadTokensAsync(response);
    }

    [Fact]
    public async Task Refresh_RotatesToken_AndIssuesNewPair()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateClient(Cookieless);

        var initial = await factory.LoginAsync(email);
        var rotated = await RefreshAsync(client, initial.RefreshToken);

        Assert.NotEqual(initial.RefreshToken, rotated.RefreshToken);
        Assert.False(string.IsNullOrWhiteSpace(rotated.AccessToken));

        // The new refresh token itself works.
        var rotatedAgain = await RefreshAsync(client, rotated.RefreshToken);
        Assert.NotEqual(rotated.RefreshToken, rotatedAgain.RefreshToken);
    }

    [Fact]
    public async Task Refresh_WithRotatedToken_IsRejected()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateClient(Cookieless);

        var initial = await factory.LoginAsync(email);
        await RefreshAsync(client, initial.RefreshToken); // rotates initial → revoked

        // Replaying the now-revoked original token must fail.
        var replay = await client.PostRefreshAsync(initial.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Fact]
    public async Task Refresh_WithGarbageToken_IsRejected()
    {
        var client = factory.CreateClient(Cookieless);
        var response = await client.PostRefreshAsync("not-a-real-token");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_RevokesRefreshToken()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);

        var tokens = await factory.LoginAsync(email);
        var authed = factory.CreateAuthedClient(tokens.AccessToken);

        // Logout is an authenticated write endpoint (Idempotency-Key + CSRF header).
        var logout = await authed.PostLogoutAsync(Guid.NewGuid().ToString(), tokens.RefreshToken);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        // The revoked token can no longer be refreshed.
        var afterLogout = await factory.CreateClient(Cookieless).PostRefreshAsync(tokens.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }
}
