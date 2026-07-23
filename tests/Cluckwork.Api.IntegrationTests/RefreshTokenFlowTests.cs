namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
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

    // #176 — a token rotated moments ago whose replacement is still the live tip
    // is a BENIGN concurrent/dead-tab retry (the #169 residual), not a replay: the
    // caller is handed a fresh token instead of the session being torn down.
    [Fact]
    public async Task Refresh_ImmediateReplayWithinGrace_Succeeds_WithoutRevokingTheSession()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateClient(Cookieless);

        var initial = await factory.LoginAsync(email);
        await RefreshAsync(client, initial.RefreshToken); // initial → live (delivered)

        // The dead tab's retry: it still holds `initial` (never saw the rotation).
        // Within the grace window this is honoured rather than read as theft.
        var replay = await client.PostRefreshAsync(initial.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        // The handed-back token is a working member of a live session — nothing
        // was revoked (a family revocation would make this refresh 401).
        var handed = await TestHarness.ReadTokensAsync(replay);
        var next = await client.PostRefreshAsync(handed.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    // #176 — theft-detection stays strict once the chain has moved on: replaying a
    // token whose replacement is itself already rotated away (not the live tip) is
    // a genuine replay and revokes the WHOLE family, not just the replayed token.
    [Fact]
    public async Task Refresh_ReplayAfterChainMovedOn_RevokesTheWholeFamily()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateClient(Cookieless);

        var initial = await factory.LoginAsync(email);
        var r1 = await RefreshAsync(client, initial.RefreshToken); // initial → r1
        var live = await RefreshAsync(client, r1.RefreshToken);     // r1 → r2 (r1 no longer the tip)

        // Replaying `initial` now: its replacement r1 is already revoked, so this
        // is not a benign grace retry — it is a replay → revoke the family.
        var replay = await client.PostRefreshAsync(initial.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // The still-live tip r2 is dead too — the whole session is torn down.
        var afterCascade = await client.PostRefreshAsync(live.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, afterCascade.StatusCode);
    }

    [Fact]
    public async Task Refresh_WithGarbageToken_IsRejected()
    {
        var client = factory.CreateClient(Cookieless);
        var response = await client.PostRefreshAsync("not-a-real-token");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_RevokesRefreshToken_CookieAuthenticated_NoBearerOrKey()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);

        var tokens = await factory.LoginAsync(email);

        // The real SPA logout: cookie + CSRF header, no bearer and no
        // Idempotency-Key. It must succeed (an authenticated/keyless logout would
        // 400 at the idempotency middleware — #145 review) and revoke the token.
        var logout = await factory.CreateClient(Cookieless).PostLogoutAsync(tokens.RefreshToken);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        // The revoked token can no longer be refreshed.
        var afterLogout = await factory.CreateClient(Cookieless).PostRefreshAsync(tokens.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }
}

// #176 — the idempotency grace is configurable; with it DISABLED (0s) even an
// immediate replay is strict theft-detection. Proves the grace gate is
// load-bearing (not that every revoked token is always accepted).
public sealed class RefreshGraceDisabledFactory : CluckworkWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Jwt:RefreshReuseGraceSeconds", "0");
    }
}

public sealed class RefreshGraceDisabledTests(RefreshGraceDisabledFactory factory)
    : IClassFixture<RefreshGraceDisabledFactory>
{
    private static readonly WebApplicationFactoryClientOptions Cookieless =
        new() { HandleCookies = false };

    [Fact]
    public async Task Refresh_ImmediateReplay_WithGraceDisabled_RevokesTheWholeFamily()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateClient(Cookieless);

        var initial = await factory.LoginAsync(email);
        var live = await client.PostRefreshAsync(initial.RefreshToken); // initial → live
        live.EnsureSuccessStatusCode();
        var liveTokens = await TestHarness.ReadTokensAsync(live);

        // Grace off → an immediate replay is strict theft, no benign window.
        var replay = await client.PostRefreshAsync(initial.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // Family revoked: the live tip is dead too.
        var afterCascade = await client.PostRefreshAsync(liveTokens.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, afterCascade.StatusCode);
    }
}
