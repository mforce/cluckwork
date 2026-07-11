namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;

// tech spec §7.4: refresh tokens are durable and rotating. Refresh issues a new pair,
// the old token is single-use, reuse is rejected, and logout revokes.
[Collection(IntegrationCollection.Name)]
public sealed class RefreshTokenFlowTests(CluckworkWebApplicationFactory factory)
{
    private async Task<TokenPairDto> RefreshAsync(HttpClient client, string refreshToken)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenPairDto>())!;
    }

    [Fact]
    public async Task Refresh_RotatesToken_AndIssuesNewPair()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateClient();

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
        var client = factory.CreateClient();

        var initial = await factory.LoginAsync(email);
        await RefreshAsync(client, initial.RefreshToken); // rotates initial → revoked

        // Replaying the now-revoked original token must fail.
        var replay = await client.PostAsJsonAsync(
            "/api/v1/auth/refresh", new { refreshToken = initial.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Fact]
    public async Task Refresh_WithGarbageToken_IsRejected()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/refresh", new { refreshToken = "not-a-real-token" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_RevokesRefreshToken()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);

        var tokens = await factory.LoginAsync(email);
        var authed = factory.CreateAuthedClient(tokens.AccessToken);

        // Logout is an authenticated write endpoint, so it requires an Idempotency-Key.
        var logout = await authed.PostWithKeyAsync(
            "/api/v1/auth/logout", Guid.NewGuid().ToString(), new { refreshToken = tokens.RefreshToken });
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        // The revoked token can no longer be refreshed.
        var afterLogout = await factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/refresh", new { refreshToken = tokens.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }
}
