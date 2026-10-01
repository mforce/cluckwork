namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.Endpoints.Auth;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

// tech spec §7.4: refresh tokens are durable and rotating. Refresh issues a new
// pair, the old token is single-use, reuse is rejected, and logout revokes.
// Since #145 the refresh token travels in an HttpOnly cookie, never the body.
[Collection(IntegrationCollection.Name)]
public sealed class RefreshTokenFlowTests(CluckworkWebApplicationFactory factory)
{
    private static readonly WebApplicationFactoryClientOptions Cookieless =
        new() { HandleCookies = false };

    private async Task<TokenPairDto> RefreshAsync(HttpClient client, string refreshToken, Guid accountId)
    {
        var response = await client.PostRefreshAsync(refreshToken, expectedAccount: accountId.ToString());
        response.EnsureSuccessStatusCode();
        return await TestHarness.ReadTokensAsync(response);
    }

    [Fact]
    public async Task Refresh_RotatesToken_AndIssuesNewPair()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateClient(Cookieless);

        var initial = await factory.LoginAsync(email);
        var rotated = await RefreshAsync(client, initial.RefreshToken, accountId);

        Assert.NotEqual(initial.RefreshToken, rotated.RefreshToken);
        Assert.False(string.IsNullOrWhiteSpace(rotated.AccessToken));

        // The new refresh token itself works.
        var rotatedAgain = await RefreshAsync(client, rotated.RefreshToken, accountId);
        Assert.NotEqual(rotated.RefreshToken, rotatedAgain.RefreshToken);
    }

    // #176 — a token rotated moments ago whose replacement is still the live tip
    // is a BENIGN concurrent/dead-tab retry (the #169 residual), not a replay: the
    // caller is handed a fresh token and NO family revocation happens.
    [Fact]
    public async Task Refresh_ImmediateReplayWithinGrace_Succeeds_WithoutRevokingTheSession()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateClient(Cookieless);

        // A SECOND, independent session for the same user (another device), logged
        // in BEFORE the grace retry — so it would die if grace wrongly revoked the
        // family (a "revoke-all-then-mint" bug that a self-check couldn't catch).
        var sibling = await factory.LoginAsync(email);

        var initial = await factory.LoginAsync(email);
        await RefreshAsync(client, initial.RefreshToken, accountId); // initial → live (delivered)

        // The dead tab's retry: it still holds `initial` (never saw the rotation).
        // Within the grace window this is honoured rather than read as theft.
        var replay = await client.PostRefreshAsync(initial.RefreshToken, expectedAccount: accountId.ToString());
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        // The handed-back token works, AND the pre-existing sibling session is
        // untouched — proving no family revocation occurred.
        var handed = await TestHarness.ReadTokensAsync(replay);
        Assert.Equal(HttpStatusCode.OK, (await client.PostRefreshAsync(handed.RefreshToken, expectedAccount: accountId.ToString())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostRefreshAsync(sibling.RefreshToken, expectedAccount: accountId.ToString())).StatusCode);
    }

    // #176 — the grace is bounded to ONE hop off a normal rotation: the token
    // revoked BY a grace-advance is marked and can never itself be graced, so a
    // stolen token cannot be leap-frogged down the chain to extend a session
    // (the exact HIGH the 4-way review + a reproduction test caught).
    [Fact]
    public async Task Refresh_GraceCannotBeLeapFroggedDownTheChain_RevokesTheFamily()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateClient(Cookieless);

        var t0 = await factory.LoginAsync(email);
        var t1 = await RefreshAsync(client, t0.RefreshToken, accountId);       // t0 → t1 (normal)

        // Dead-tab grace retry: replay t0 → t2. This marks t1 as revoked-by-grace.
        var graceResp = await client.PostRefreshAsync(t0.RefreshToken, expectedAccount: accountId.ToString());
        Assert.Equal(HttpStatusCode.OK, graceResp.StatusCode);
        var t2 = await TestHarness.ReadTokensAsync(graceResp);

        // The leap-frog: present t1 (the link revoked by the grace advance). It
        // must NOT grace a second time — it is theft → the family is revoked.
        var leap = await client.PostRefreshAsync(t1.RefreshToken, expectedAccount: accountId.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, leap.StatusCode);

        // Cascade: the live tip t2 is dead too — the whole session is torn down.
        var afterCascade = await client.PostRefreshAsync(t2.RefreshToken, expectedAccount: accountId.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, afterCascade.StatusCode);
    }

    // #176 — two concurrent presentations of the SAME token must never fork the
    // chain into two live sessions. The per-token optimistic-concurrency stamp
    // makes consumption an atomic compare-and-swap: a true-overlap loser fails
    // closed (401, never a 500), a read-after-commit loser advances the single
    // chain via grace — either way exactly ONE live tip remains, never two.
    // (With 3+ concurrent, a late replay can legitimately find the replacement
    // already consumed and trip the theft response to 0 live — also non-forking;
    // two is the deterministic boundary that isolates the anti-fork guarantee.)
    //
    // #468 — that boundary claim was NOT true when written: the read-after-commit
    // loser reached the theft response instead of grace whenever it had captured
    // its clock before the winner captured theirs, which made this test flaky at
    // 0 live rather than 1. Since the grace window is measured from the read it
    // holds as stated. Both interleavings are pinned deterministically, without
    // racing, in RefreshGraceClockRaceTests — this test races them for real, so
    // it is the one that notices if the ordering stops being an invariant.
    [Fact]
    public async Task Refresh_ConcurrentPresentationsOfSameToken_NeverForkIntoTwoSessions()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateClient(Cookieless);

        var t0 = await factory.LoginAsync(email);

        var responses = await Task.WhenAll(
            client.PostRefreshAsync(t0.RefreshToken, expectedAccount: accountId.ToString()),
            client.PostRefreshAsync(t0.RefreshToken, expectedAccount: accountId.ToString()));

        // A concurrency conflict must fail closed (401), never surface as a 500.
        Assert.All(responses, r => Assert.True(
            r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Unauthorized,
            $"unexpected {(int)r.StatusCode} — a concurrency conflict must fail closed, not 500"));
        Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.OK);

        // The invariant: exactly one live (non-revoked) token remains — the race
        // never minted a second, forked session.
        var active = 0;
        await factory.WithTenantScopeAsync(accountId, async db =>
            active = await db.RefreshTokens.CountAsync(t => t.AccountId == accountId && t.RevokedAt == null));
        Assert.Equal(1, active);
    }

    // #176 — theft-detection stays strict once the chain has moved on: replaying a
    // token whose replacement is itself already rotated away (not the live tip) is
    // a genuine replay and revokes the WHOLE family, not just the replayed token.
    [Fact]
    public async Task Refresh_ReplayAfterChainMovedOn_RevokesTheWholeFamily()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateClient(Cookieless);

        var initial = await factory.LoginAsync(email);
        var r1 = await RefreshAsync(client, initial.RefreshToken, accountId); // initial → r1
        var live = await RefreshAsync(client, r1.RefreshToken, accountId);     // r1 → r2 (r1 no longer the tip)

        // Replaying `initial` now: its replacement r1 is already revoked, so this
        // is not a benign grace retry — it is a replay → revoke the family.
        var replay = await client.PostRefreshAsync(initial.RefreshToken, expectedAccount: accountId.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // The still-live tip r2 is dead too — the whole session is torn down.
        var afterCascade = await client.PostRefreshAsync(live.RefreshToken, expectedAccount: accountId.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, afterCascade.StatusCode);
    }

    [Fact]
    public async Task Refresh_WithGarbageToken_IsRejected()
    {
        var client = factory.CreateClient(Cookieless);
        var fakeAccount = Guid.NewGuid();
        var response = await client.PostRefreshAsync("not-a-real-token", expectedAccount: fakeAccount.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }


    [Fact]
    public async Task Logout_RevokesRefreshToken_CookieAuthenticated_NoBearerOrKey()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);

        var tokens = await factory.LoginAsync(email);

        // The real SPA logout: cookie + CSRF header, no bearer and no
        // Idempotency-Key. It must succeed (an authenticated/keyless logout would
        // 400 at the idempotency middleware — #145 review) and revoke the token.
        var logout = await factory.CreateClient(Cookieless).PostLogoutAsync(tokens.RefreshToken, accountId: accountId);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        // The revoked token can no longer be refreshed.
        var afterLogout = await factory.CreateClient(Cookieless)
            .PostRefreshAsync(tokens.RefreshToken, expectedAccount: accountId.ToString());
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
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateClient(Cookieless);

        var initial = await factory.LoginAsync(email);
        var live = await client.PostRefreshAsync(initial.RefreshToken, expectedAccount: accountId.ToString()); // initial → live
        live.EnsureSuccessStatusCode();
        var liveTokens = await TestHarness.ReadTokensAsync(live);

        // Grace off → an immediate replay is strict theft, no benign window.
        var replay = await client.PostRefreshAsync(initial.RefreshToken, expectedAccount: accountId.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // Family revoked: the live tip is dead too.
        var afterCascade = await client.PostRefreshAsync(liveTokens.RefreshToken, expectedAccount: accountId.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, afterCascade.StatusCode);
    }
}
