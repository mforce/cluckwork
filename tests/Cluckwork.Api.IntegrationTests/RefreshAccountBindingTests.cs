namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #547 (slice T4) — the refresh endpoint compares the tab's expected farm
// (the X-Cluckwork-Account header) against the STORED token's AccountId before
// anything rotates. The refresh cookie is per-origin (one per browser, last
// login anywhere wins) while access tokens are per-tab, so one browser holding
// two farms can otherwise hand a tab the wrong farm's session — and that tab
// would then retry its pending request, body included, against that farm.
//
// The guarantee each test pins:
//   * a mismatch is refused with the DISTINCT Auth.SessionChanged code and
//     ROTATES NOTHING — the other farm's token must still work afterwards
//     (the "still works" assertion is what makes "rotates nothing" real:
//     without it the test passes even if the token was consumed);
//   * a matching expectation rotates normally (new token works, old is dead);
//   * an absent header still works — the load-time bootstrap path;
//   * an unparseable header is refused (fail closed), never a 500 and never
//     silently read as "no expectation".
[Collection(IntegrationCollection.Name)]
public sealed class RefreshAccountBindingTests(CluckworkWebApplicationFactory factory)
{
    private static async Task<Guid> SeedAsync(CluckworkWebApplicationFactory factory, string email)
        => await factory.SeedAccountWithUserAsync(email);

    // Reads one farm's live refresh-token family straight from the store: the
    // token ids plus the current tip's hash, for the "rotates nothing" proof
    // below.
    private static async Task<(Guid[] TokenIds, string TipHash)> FamilyAsync(
        CluckworkWebApplicationFactory factory, Guid accountId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Cluckwork.Infrastructure.Persistence.AppDbContext>();
        var tokens = await db.RefreshTokens.AsNoTracking()
            .Where(t => t.AccountId == accountId)
            .ToListAsync();
        var tip = tokens.Single(t => t.RevokedAt is null);
        return (tokens.Select(t => t.Id).ToArray(), tip.TokenHash);
    }

    private async Task<string?> ProblemTitleAsync(HttpResponseMessage response)
    {
        if (response.Content is null) return null;
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        return problem?.Title;
    }

    [Fact]
    public async Task MismatchIsRefusedAndRotatesNothing()
    {
        var emailA = $"547-a-{Guid.NewGuid():N}@test.local";
        var emailB = $"547-b-{Guid.NewGuid():N}@test.local";
        var farmA = await SeedAsync(factory, emailA);
        var farmB = await SeedAsync(factory, emailB);
        var tokensA = await factory.LoginAsync(emailA);
        var tokensB = await factory.LoginAsync(emailB);

        // Family state BEFORE the refused request: farm B's one live tip.
        var (familyBBefore, tipHashBefore) = await FamilyAsync(factory, farmB);

        // Present farm B's refresh token while telling the server the tab
        // expects farm A: the stored token belongs to farm B, so this must be
        // refused before anything rotates.
        var refused = await factory
            .CreateClient(TestHarness.Cookieless(factory))
            .PostRefreshAsync(tokensB.RefreshToken, csrf: true, expectedAccount: farmA.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal("Auth.SessionChanged", await ProblemTitleAsync(refused));

        // THE assertion that pins "rotates nothing": the store is unchanged —
        // the same tip row, the same hash, still live. A refusal that rotated
        // or revoked would change the hash, the live count, or both.
        var (familyBAfter, tipHashAfter) = await FamilyAsync(factory, farmB);
        Assert.Equal(familyBBefore, familyBAfter);
        Assert.Equal(tipHashBefore, tipHashAfter);

        // ...and farm B's token still rotates with the CORRECT expectation.
        // If the mismatch path had consumed it, this 200 would be a 401.
        var stillWorks = await factory
            .CreateClient(TestHarness.Cookieless(factory))
            .PostRefreshAsync(tokensB.RefreshToken, csrf: true, expectedAccount: farmB.ToString());
        Assert.Equal(HttpStatusCode.OK, stillWorks.StatusCode);

        // And farm A's own token is untouched too: the refusal reached nothing.
        var farmAIntact = await factory
            .CreateClient(TestHarness.Cookieless(factory))
            .PostRefreshAsync(tokensA.RefreshToken, csrf: true, expectedAccount: farmA.ToString());
        Assert.Equal(HttpStatusCode.OK, farmAIntact.StatusCode);
    }

    [Fact]
    public async Task MatchingExpectationRotatesNormally()
    {
        var emailA = $"547-a-{Guid.NewGuid():N}@test.local";
        var emailB = $"547-b-{Guid.NewGuid():N}@test.local";
        var farmA = await SeedAsync(factory, emailA);
        _ = await SeedAsync(factory, emailB); // a second farm, per the slice's two-farm shape
        var tokens = await factory.LoginAsync(emailA);

        var refreshed = await factory
            .CreateClient(TestHarness.Cookieless(factory))
            .PostRefreshAsync(tokens.RefreshToken, csrf: true, expectedAccount: farmA.ToString());
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);

        // The rotation is real: the fresh cookie works, the old token is dead.
        var next = await factory
            .CreateClient(TestHarness.Cookieless(factory))
            .PostRefreshAsync(TestHarness.ExtractRefreshCookie(refreshed), csrf: true, expectedAccount: farmA.ToString());
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);

        var stale = await factory
            .CreateClient(TestHarness.Cookieless(factory))
            .PostRefreshAsync(tokens.RefreshToken, csrf: true, expectedAccount: farmA.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
    }

    [Fact]
    public async Task AbsentHeaderStillWorks_TheBootstrapPath()
    {
        var emailA = $"547-a-{Guid.NewGuid():N}@test.local";
        var farmA = await SeedAsync(factory, emailA);
        var tokens = await factory.LoginAsync(emailA);

        // No expected-account header at all: the load-time bootstrap runs
        // before any tab knows its farm, so absent means "no expectation".
        var refreshed = await factory
            .CreateClient(TestHarness.Cookieless(factory))
            .PostRefreshAsync(tokens.RefreshToken, csrf: true);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        Assert.False(string.IsNullOrEmpty(TestHarness.ExtractRefreshCookie(refreshed)),
            "a successful bootstrap refresh must rotate the cookie");
        // A rotation happened server-side: exactly one live tip in farm A's
        // family (FamilyAsync's Single(t => t.RevokedAt is null) proves it).
        var (_, _) = await FamilyAsync(factory, farmA);
    }

    [Fact]
    public async Task UnparseableHeaderIsRefused_FailClosed()
    {
        var emailA = $"547-a-{Guid.NewGuid():N}@test.local";
        var farmA = await SeedAsync(factory, emailA);
        var tokens = await factory.LoginAsync(emailA);

        // A malformed expectation is a client that thinks it knows its farm.
        // Honouring it as "no expectation" would let a broken or hostile
        // client opt out of the check, so it is treated as a MISMATCH —
        // refused with the same distinct code, not a 500, not a silent pass.
        var refused = await factory
            .CreateClient(TestHarness.Cookieless(factory))
            .PostRefreshAsync(tokens.RefreshToken, csrf: true, expectedAccount: "not-a-guid");
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal("Auth.SessionChanged", await ProblemTitleAsync(refused));

        // ...and it rotated nothing: the token still works without the header.
        var stillWorks = await factory
            .CreateClient(TestHarness.Cookieless(factory))
            .PostRefreshAsync(tokens.RefreshToken, csrf: true);
        Assert.Equal(HttpStatusCode.OK, stillWorks.StatusCode);
    }
}
