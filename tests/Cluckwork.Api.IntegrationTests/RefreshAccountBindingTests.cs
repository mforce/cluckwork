namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using Cluckwork.Api.Endpoints.Auth;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #547 (slice T4) — the refresh endpoint compares the tab's expected farm
// (the X-Cluckwork-Account header) against the STORED token's AccountId before
// anything rotates. Per-farm cookie names make a normal cross-farm selection
// impossible; this remains defence-in-depth against a malformed or misplaced
// cookie before a tab retries its pending request, body included.
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
        var fresh = (await TestHarness.ReadTokensAsync(refreshed)).RefreshToken;
        var next = await factory
            .CreateClient(TestHarness.Cookieless(factory))
            .PostRefreshAsync(fresh, csrf: true, expectedAccount: farmA.ToString());
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
            .PostRefreshRawAsync(
                AuthCookies.RefreshCookieNameFor(farmA) + "=" + tokens.RefreshToken);
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
        // The unparseable header is refused BEFORE the cookie is read, so the
        // cookie name used to build the request is irrelevant to the refusal —
        // it must still be well-formed so the request is well-formed. Use farmA.
        var refused = await factory
            .CreateClient(TestHarness.Cookieless(factory))
            .PostRefreshRawAsync(
                AuthCookies.RefreshCookieNameFor(farmA) + "=" + tokens.RefreshToken,
                expectedAccount: "not-a-guid");
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal("Auth.SessionChanged", await ProblemTitleAsync(refused));

        // ...and it rotated nothing: the token still works without the header.
        var stillWorks = await factory
            .CreateClient(TestHarness.Cookieless(factory))
            .PostRefreshAsync(tokens.RefreshToken, expectedAccount: farmA.ToString());
        Assert.Equal(HttpStatusCode.OK, stillWorks.StatusCode);
    }
}

// #532 — per-farm cookie names make cross-farm selection structural: the
// X-Cluckwork-Account header chooses one cookie, while a headerless bootstrap
// is accepted only when the browser holds exactly one farm session.
[Collection(IntegrationCollection.Name)]
public sealed class PerFarmRefreshCookieTests(CluckworkWebApplicationFactory factory)
{
    // The API emits Secure cookies in the integration environment, while the
    // in-process test transport itself is HTTP. Keep a real CookieContainer at
    // an HTTPS logical origin, then attach exactly the header it selects to the
    // cookieless transport. This exercises browser storage/path/expiry behavior
    // without weakening the production Secure attribute for tests.
    private sealed class TestBrowser(CluckworkWebApplicationFactory factory)
    {
        private static readonly Uri CookieOrigin = new("https://cluckwork.test/api/v1/auth/");
        private readonly CookieContainer cookies = new();
        private readonly HttpClient client = factory.CreateClient(TestHarness.Cookieless(factory));

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
        {
            var cookieHeader = cookies.GetCookieHeader(CookieOrigin);
            if (!string.IsNullOrEmpty(cookieHeader))
                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);

            var response = await client.SendAsync(request);
            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                foreach (var setCookie in setCookies)
                    cookies.SetCookies(CookieOrigin, setCookie);
            }
            return response;
        }

        public string? CookieValue(Guid accountId) =>
            cookies.GetCookies(CookieOrigin)[AuthCookies.RefreshCookieNameFor(accountId)]?.Value;
    }

    private static async Task<(Guid Farm, string RefreshToken, string AccessToken)> LoginAsync(
        CluckworkWebApplicationFactory factory, TestBrowser browser, string email)
    {
        var farm = await factory.SeedAccountWithUserAsync(email);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new
            {
                farmCode = await factory.FarmCodeForAsync(email),
                email,
                password = TestHarness.Password,
            }),
        };
        var response = await browser.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var tokens = await TestHarness.ReadTokensAsync(response);
        Assert.False(string.IsNullOrEmpty(tokens.RefreshToken));
        return (farm, tokens.RefreshToken, tokens.AccessToken);
    }

    private static Task<HttpResponseMessage> RefreshBrowserAsync(
        TestBrowser browser, Guid? accountId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        request.Headers.Add(AuthCookies.CsrfHeaderName, "1");
        if (accountId is { } account)
            request.Headers.Add(AuthCookies.ExpectedAccountHeaderName, account.ToString());
        return browser.SendAsync(request);
    }

    private static void AssertNoSetCookie(HttpResponseMessage response, string cookieName)
    {
        Assert.False(
            response.Headers.TryGetValues("Set-Cookie", out var cookies)
            && cookies.Any(c => c.StartsWith(cookieName + "=", StringComparison.Ordinal)),
            $"response must not set or clear '{cookieName}'");
    }

    private static void AssertClearsCookie(HttpResponseMessage response, string cookieName)
    {
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cookies));
        var cookie = cookies!.Single(c => c.StartsWith(cookieName + "=", StringComparison.Ordinal));
        Assert.Contains("expires=Thu, 01 Jan 1970", cookie, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string?> ProblemTitleAsync(HttpResponseMessage response)
    {
        if (response.Content is null) return null;
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        return problem?.Title;
    }

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

    private static async Task<int> LogoutEpochAsync(
        CluckworkWebApplicationFactory factory, Guid accountId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Cluckwork.Infrastructure.Persistence.AppDbContext>();
        return await db.Users.AsNoTracking()
            .Where(u => u.AccountId == accountId)
            .Select(u => u.StepUpLogoutEpoch)
            .SingleAsync();
    }

    [Fact]
    public async Task TwoFarms_RefreshRotatesOnlyItsOwnCookie_OtherCookieByteIdentical()
    {
        // One client means one real CookieContainer: both logins land in the
        // same browser jar, and every refresh sends both path-matching cookies.
        var browser = new TestBrowser(factory);
        var (farmA, tokenA0, _) = await LoginAsync(
            factory, browser, $"532-a-{Guid.NewGuid():N}@test.local");
        var (farmB, tokenB0, _) = await LoginAsync(
            factory, browser, $"532-b-{Guid.NewGuid():N}@test.local");
        var cookieA = AuthCookies.RefreshCookieNameFor(farmA);
        var cookieB = AuthCookies.RefreshCookieNameFor(farmB);

        var refreshA = await RefreshBrowserAsync(browser, farmA);
        Assert.Equal(HttpStatusCode.OK, refreshA.StatusCode);
        var tokenA1 = TestHarness.ExtractRefreshCookie(refreshA, farmA);
        Assert.False(string.IsNullOrEmpty(tokenA1));
        Assert.NotEqual(tokenA0, tokenA1);
        AssertNoSetCookie(refreshA, cookieB);
        Assert.Equal(tokenB0, browser.CookieValue(farmB));

        // Farm B's original byte sequence remains usable. This is the durable
        // proof that farm A neither rotated nor revoked B's family.
        var refreshB = await RefreshBrowserAsync(browser, farmB);
        Assert.Equal(HttpStatusCode.OK, refreshB.StatusCode);
        AssertNoSetCookie(refreshB, cookieA);
        Assert.NotEqual(tokenB0, TestHarness.ExtractRefreshCookie(refreshB, farmB));

        // Do not assert that tokenA0 is immediately rejected: #176's existing
        // grace contract deliberately accepts an immediate duplicate request
        // and returns the already-minted replacement without forking a family.
    }

    [Fact]
    public async Task RefreshNamingAFarmWithNoCookie_IsSessionChanged_AndSetsNoCookie()
    {
        var client = factory.CreateClient(TestHarness.Cookieless(factory));
        var loginClient = new TestBrowser(factory);
        var (farmA, tokenA, _) = await LoginAsync(
            factory, loginClient, $"532-c-{Guid.NewGuid():N}@test.local");
        var farmB = await factory.SeedAccountWithUserAsync(
            $"532-d-{Guid.NewGuid():N}@test.local");

        var refused = await client.PostRefreshRawAsync(
            AuthCookies.RefreshCookieNameFor(farmA) + "=" + tokenA,
            expectedAccount: farmB.ToString());

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal("Auth.SessionChanged", await ProblemTitleAsync(refused));
        Assert.False(refused.Headers.TryGetValues("Set-Cookie", out _));

        var stillWorks = await client.PostRefreshRawAsync(
            AuthCookies.RefreshCookieNameFor(farmA) + "=" + tokenA,
            expectedAccount: farmA.ToString());
        Assert.Equal(HttpStatusCode.OK, stillWorks.StatusCode);
    }

    [Fact]
    public async Task Bootstrap_WithExactlyOneCookie_Succeeds()
    {
        var browser = new TestBrowser(factory);
        var (farm, _, _) = await LoginAsync(
            factory, browser, $"532-e-{Guid.NewGuid():N}@test.local");

        var refreshed = await RefreshBrowserAsync(browser);

        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        Assert.False(string.IsNullOrEmpty(TestHarness.ExtractRefreshCookie(refreshed, farm)));
    }

    [Fact]
    public async Task Bootstrap_WithTwoCookies_IsFarmSelectionRequired_AndTouchesNothing()
    {
        var client = factory.CreateClient(TestHarness.Cookieless(factory));
        var loginClient = new TestBrowser(factory);
        var (farmA, tokenA, _) = await LoginAsync(
            factory, loginClient, $"532-f-{Guid.NewGuid():N}@test.local");
        var (farmB, tokenB, _) = await LoginAsync(
            factory, loginClient, $"532-g-{Guid.NewGuid():N}@test.local");
        var (familyABefore, tipABefore) = await FamilyAsync(factory, farmA);
        var (familyBBefore, tipBBefore) = await FamilyAsync(factory, farmB);

        var refused = await client.PostRefreshRawAsync(
            $"{AuthCookies.RefreshCookieNameFor(farmA)}={tokenA}; "
            + $"{AuthCookies.RefreshCookieNameFor(farmB)}={tokenB}");

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal(AuthEndpoints.FarmSelectionRequiredCode, await ProblemTitleAsync(refused));
        Assert.False(refused.Headers.TryGetValues("Set-Cookie", out _));

        var (familyAAfter, tipAAfter) = await FamilyAsync(factory, farmA);
        var (familyBAfter, tipBAfter) = await FamilyAsync(factory, farmB);
        Assert.Equal(familyABefore, familyAAfter);
        Assert.Equal(tipABefore, tipAAfter);
        Assert.Equal(familyBBefore, familyBAfter);
        Assert.Equal(tipBBefore, tipBAfter);
    }

    [Fact]
    public async Task Logout_ClearsOnlyTheCallerFarmCookie_OtherFarmStillRefreshes()
    {
        var browser = new TestBrowser(factory);
        var (farmA, _, accessA) = await LoginAsync(
            factory, browser, $"532-h-{Guid.NewGuid():N}@test.local");
        var (farmB, _, _) = await LoginAsync(
            factory, browser, $"532-i-{Guid.NewGuid():N}@test.local");
        var cookieA = AuthCookies.RefreshCookieNameFor(farmA);
        var cookieB = AuthCookies.RefreshCookieNameFor(farmB);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        request.Headers.Add(AuthCookies.CsrfHeaderName, "1");
        request.Headers.Add(AuthCookies.ExpectedAccountHeaderName, farmA.ToString());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessA);
        var logout = await browser.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        AssertClearsCookie(logout, cookieA);
        AssertNoSetCookie(logout, cookieB);
        Assert.Equal(HttpStatusCode.OK, (await RefreshBrowserAsync(browser, farmB)).StatusCode);
    }

    [Fact]
    public async Task ChangePassword_FarmA_LeavesFarmBCookieUntouched_AndStillUsable()
    {
        var browser = new TestBrowser(factory);
        var (farmA, _, accessA) = await LoginAsync(
            factory, browser, $"532-j-{Guid.NewGuid():N}@test.local");
        var (farmB, _, _) = await LoginAsync(
            factory, browser, $"532-k-{Guid.NewGuid():N}@test.local");
        var cookieA = AuthCookies.RefreshCookieNameFor(farmA);
        var cookieB = AuthCookies.RefreshCookieNameFor(farmB);
        var newPassword = $"Aa1!{Guid.NewGuid():N}";

        var change = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/change-password")
        {
            Content = JsonContent.Create(new
            {
                currentPassword = TestHarness.Password,
                newPassword,
            }),
        };
        change.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessA);
        var response = await browser.SendAsync(change);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var setCookies = response.Headers.GetValues("Set-Cookie").ToList();
        Assert.Contains(setCookies, c => c.StartsWith(cookieA + "=", StringComparison.Ordinal));
        Assert.DoesNotContain(setCookies, c =>
            c.StartsWith(AuthCookies.LegacyRefreshCookieName + "=", StringComparison.Ordinal));
        AssertNoSetCookie(response, cookieB);
        Assert.Equal(HttpStatusCode.OK, (await RefreshBrowserAsync(browser, farmB)).StatusCode);
    }

    [Fact]
    public async Task LegacyCookie_RefreshSucceeds_SetsPerFarmCookie_AndDeletesLegacy()
    {
        var loginClient = new TestBrowser(factory);
        var (farm, token, _) = await LoginAsync(
            factory, loginClient, $"532-l-{Guid.NewGuid():N}@test.local");
        var client = factory.CreateClient(TestHarness.Cookieless(factory));

        // A freshly loaded legacy session has no access token and therefore no
        // account selector. The stored token row supplies the farm on upgrade.
        var refreshed = await client.PostRefreshRawAsync(
            AuthCookies.LegacyRefreshCookieName + "=" + token);

        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var setCookies = refreshed.Headers.GetValues("Set-Cookie").ToList();
        Assert.Contains(setCookies, c => c.StartsWith(
            AuthCookies.RefreshCookieNameFor(farm) + "=", StringComparison.Ordinal));
        AssertClearsCookie(refreshed, AuthCookies.LegacyRefreshCookieName);
    }

    [Fact]
    public async Task Logout_WithOnlyALegacyCookie_ClearsAndRevokesIt()
    {
        var loginClient = new TestBrowser(factory);
        var (_, token, _) = await LoginAsync(
            factory, loginClient, $"532-legacy-logout-{Guid.NewGuid():N}@test.local");
        var client = factory.CreateClient(TestHarness.Cookieless(factory));

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        request.Headers.Add(AuthCookies.CsrfHeaderName, "1");
        request.Headers.Add(
            "Cookie", AuthCookies.LegacyRefreshCookieName + "=" + token);
        var logout = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        var afterLogout = await client.PostRefreshRawAsync(
            AuthCookies.LegacyRefreshCookieName + "=" + token);
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
        AssertClearsCookie(logout, AuthCookies.LegacyRefreshCookieName);
    }

    [Fact]
    public async Task Logout_WithSelectedFarmAndCrossFarmLegacyCookie_RevokesSelectedAndPreservesLegacySession()
    {
        var loginClient = new TestBrowser(factory);
        var (farm, farmToken, _) = await LoginAsync(
            factory, loginClient, $"532-farm-logout-{Guid.NewGuid():N}@test.local");
        var (legacyFarm, legacyToken, _) = await LoginAsync(
            factory, loginClient, $"532-mixed-legacy-logout-{Guid.NewGuid():N}@test.local");
        var client = factory.CreateClient(TestHarness.Cookieless(factory));

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        request.Headers.Add(AuthCookies.CsrfHeaderName, "1");
        request.Headers.Add(AuthCookies.ExpectedAccountHeaderName, farm.ToString());
        request.Headers.Add(
            "Cookie",
            $"{AuthCookies.RefreshCookieNameFor(farm)}={farmToken}; "
            + $"{AuthCookies.LegacyRefreshCookieName}={legacyToken}");
        var logout = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        AssertClearsCookie(logout, AuthCookies.RefreshCookieNameFor(farm));
        AssertNoSetCookie(logout, AuthCookies.LegacyRefreshCookieName);

        var farmAfterLogout = await client.PostRefreshRawAsync(
            AuthCookies.RefreshCookieNameFor(farm) + "=" + farmToken,
            expectedAccount: farm.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, farmAfterLogout.StatusCode);

        var legacyAfterLogout = await client.PostRefreshRawAsync(
            AuthCookies.LegacyRefreshCookieName + "=" + legacyToken,
            expectedAccount: legacyFarm.ToString());
        Assert.Equal(HttpStatusCode.OK, legacyAfterLogout.StatusCode);
    }

    [Fact]
    public async Task Logout_WithoutSelectorAndWithLegacyAndPerFarmCookies_PreservesBothSessions()
    {
        var loginClient = new TestBrowser(factory);
        var (legacyFarm, legacyToken, _) = await LoginAsync(
            factory, loginClient, $"570-legacy-{Guid.NewGuid():N}@test.local");
        var (otherFarm, otherToken, _) = await LoginAsync(
            factory, loginClient, $"570-other-{Guid.NewGuid():N}@test.local");
        var client = factory.CreateClient(TestHarness.Cookieless(factory));

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        request.Headers.Add(AuthCookies.CsrfHeaderName, "1");
        request.Headers.Add(
            "Cookie",
            $"{AuthCookies.LegacyRefreshCookieName}={legacyToken}; "
            + $"{AuthCookies.RefreshCookieNameFor(otherFarm)}={otherToken}");
        var logout = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        var legacyAfterLogout = await client.PostRefreshRawAsync(
            AuthCookies.LegacyRefreshCookieName + "=" + legacyToken,
            expectedAccount: legacyFarm.ToString());
        Assert.Equal(HttpStatusCode.OK, legacyAfterLogout.StatusCode);

        var otherAfterLogout = await client.PostRefreshRawAsync(
            AuthCookies.RefreshCookieNameFor(otherFarm) + "=" + otherToken,
            expectedAccount: otherFarm.ToString());
        Assert.Equal(HttpStatusCode.OK, otherAfterLogout.StatusCode);

        AssertNoSetCookie(logout, AuthCookies.LegacyRefreshCookieName);
        AssertNoSetCookie(logout, AuthCookies.RefreshCookieNameFor(otherFarm));
    }

    [Fact]
    public async Task Logout_WithTheSameTokenUnderSelectedAndLegacyNames_RevokesOnceAndClearsBoth()
    {
        var loginClient = new TestBrowser(factory);
        var (farm, token, _) = await LoginAsync(
            factory, loginClient, $"569-same-token-{Guid.NewGuid():N}@test.local");
        var epochBefore = await LogoutEpochAsync(factory, farm);
        var client = factory.CreateClient(TestHarness.Cookieless(factory));

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        request.Headers.Add(AuthCookies.CsrfHeaderName, "1");
        request.Headers.Add(AuthCookies.ExpectedAccountHeaderName, farm.ToString());
        request.Headers.Add(
            "Cookie",
            $"{AuthCookies.RefreshCookieNameFor(farm)}={token}; "
            + $"{AuthCookies.LegacyRefreshCookieName}={token}");
        var logout = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        AssertClearsCookie(logout, AuthCookies.RefreshCookieNameFor(farm));
        AssertClearsCookie(logout, AuthCookies.LegacyRefreshCookieName);
        Assert.Equal(epochBefore + 1, await LogoutEpochAsync(factory, farm));

        var afterLogout = await client.PostRefreshRawAsync(
            AuthCookies.LegacyRefreshCookieName + "=" + token,
            expectedAccount: farm.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }

    [Fact]
    public async Task Logout_WithForeignTokenUnderSelectedAndLegacyNames_PreservesForeignSession()
    {
        var loginClient = new TestBrowser(factory);
        var (selectedFarm, _, _) = await LoginAsync(
            factory, loginClient, $"569-selected-equal-{Guid.NewGuid():N}@test.local");
        var (foreignFarm, foreignToken, _) = await LoginAsync(
            factory, loginClient, $"569-foreign-equal-{Guid.NewGuid():N}@test.local");
        var foreignEpochBefore = await LogoutEpochAsync(factory, foreignFarm);
        var client = factory.CreateClient(TestHarness.Cookieless(factory));

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        request.Headers.Add(AuthCookies.CsrfHeaderName, "1");
        request.Headers.Add(AuthCookies.ExpectedAccountHeaderName, selectedFarm.ToString());
        request.Headers.Add(
            "Cookie",
            $"{AuthCookies.RefreshCookieNameFor(selectedFarm)}={foreignToken}; "
            + $"{AuthCookies.LegacyRefreshCookieName}={foreignToken}");
        var logout = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        AssertNoSetCookie(logout, AuthCookies.LegacyRefreshCookieName);
        Assert.Equal(foreignEpochBefore, await LogoutEpochAsync(factory, foreignFarm));

        var foreignAfterLogout = await client.PostRefreshRawAsync(
            AuthCookies.LegacyRefreshCookieName + "=" + foreignToken,
            expectedAccount: foreignFarm.ToString());
        Assert.Equal(HttpStatusCode.OK, foreignAfterLogout.StatusCode);
    }

    [Fact]
    public async Task Logout_WithSelectedFarmAndOnlyForeignLegacyCookie_PreservesForeignSession()
    {
        var loginClient = new TestBrowser(factory);
        var (selectedFarm, _, _) = await LoginAsync(
            factory, loginClient, $"569-selected-foreign-legacy-only-{Guid.NewGuid():N}@test.local");
        var (foreignFarm, foreignToken, _) = await LoginAsync(
            factory, loginClient, $"569-foreign-legacy-only-{Guid.NewGuid():N}@test.local");
        var foreignEpochBefore = await LogoutEpochAsync(factory, foreignFarm);
        var client = factory.CreateClient(TestHarness.Cookieless(factory));

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        request.Headers.Add(AuthCookies.CsrfHeaderName, "1");
        request.Headers.Add(AuthCookies.ExpectedAccountHeaderName, selectedFarm.ToString());
        request.Headers.Add("Cookie", AuthCookies.LegacyRefreshCookieName + "=" + foreignToken);
        var logout = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        AssertNoSetCookie(logout, AuthCookies.LegacyRefreshCookieName);
        Assert.Equal(foreignEpochBefore, await LogoutEpochAsync(factory, foreignFarm));

        var foreignAfterLogout = await client.PostRefreshRawAsync(
            AuthCookies.LegacyRefreshCookieName + "=" + foreignToken,
            expectedAccount: foreignFarm.ToString());
        Assert.Equal(HttpStatusCode.OK, foreignAfterLogout.StatusCode);
    }

    [Fact]
    public async Task Logout_WithSelectedFarmAndOnlySameFarmLegacyCookie_RevokesAndClearsLegacy()
    {
        var loginClient = new TestBrowser(factory);
        var (farm, token, _) = await LoginAsync(
            factory, loginClient, $"569-selected-legacy-only-{Guid.NewGuid():N}@test.local");
        var client = factory.CreateClient(TestHarness.Cookieless(factory));

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        request.Headers.Add(AuthCookies.CsrfHeaderName, "1");
        request.Headers.Add(AuthCookies.ExpectedAccountHeaderName, farm.ToString());
        request.Headers.Add("Cookie", AuthCookies.LegacyRefreshCookieName + "=" + token);
        var logout = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        AssertClearsCookie(logout, AuthCookies.LegacyRefreshCookieName);

        var afterLogout = await client.PostRefreshRawAsync(
            AuthCookies.LegacyRefreshCookieName + "=" + token,
            expectedAccount: farm.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }

    [Fact]
    public async Task Logout_WithSelectedFarmAndRetainedRevokedLegacyToken_ClearsLegacy()
    {
        var email = $"569-retained-revoked-{Guid.NewGuid():N}@test.local";
        var loginClient = new TestBrowser(factory);
        var (farm, siblingToken, _) = await LoginAsync(factory, loginClient, email);

        var secondLogin = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new
            {
                farmCode = await factory.FarmCodeForAsync(email),
                email,
                password = TestHarness.Password,
            }),
        };
        var secondLoginResponse = await loginClient.SendAsync(secondLogin);
        secondLoginResponse.EnsureSuccessStatusCode();
        var retainedToken = (await TestHarness.ReadTokensAsync(secondLoginResponse)).RefreshToken;
        var client = factory.CreateClient(TestHarness.Cookieless(factory));

        var rotation = await client.PostRefreshRawAsync(
            AuthCookies.LegacyRefreshCookieName + "=" + retainedToken,
            expectedAccount: farm.ToString());
        Assert.Equal(HttpStatusCode.OK, rotation.StatusCode);
        var childToken = TestHarness.ExtractRefreshCookie(rotation, farm);
        Assert.False(string.IsNullOrEmpty(childToken));

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        request.Headers.Add(AuthCookies.CsrfHeaderName, "1");
        request.Headers.Add(AuthCookies.ExpectedAccountHeaderName, farm.ToString());
        request.Headers.Add("Cookie", AuthCookies.LegacyRefreshCookieName + "=" + retainedToken);
        var logout = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        AssertClearsCookie(logout, AuthCookies.LegacyRefreshCookieName);

        // Probe the unrelated session BEFORE presenting either cleared lineage
        // node. Otherwise replay detection could revoke it and make a passing
        // result say nothing about logout's own scope.
        var siblingBeforeReplay = await client.PostRefreshAsync(
            siblingToken, expectedAccount: farm.ToString());
        Assert.Equal(HttpStatusCode.OK, siblingBeforeReplay.StatusCode);
        var siblingChild = await TestHarness.ReadTokensAsync(siblingBeforeReplay);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostRefreshAsync(
            childToken, expectedAccount: farm.ToString())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostRefreshAsync(
            retainedToken, expectedAccount: farm.ToString())).StatusCode);

        // A later presentation starts from the already-severed no-pointer row,
        // which is intentionally indistinguishable from a bulk-revoked tip and
        // therefore retains #176's strict replay behavior. The sibling probe
        // above is the proof that logout itself stayed on the selected lineage.
    }

    [Fact]
    public async Task Logout_WithSelectedFarmAndPurgedLegacyToken_DoesNotClearLegacy()
    {
        var loginClient = new TestBrowser(factory);
        var (selectedFarm, selectedToken, _) = await LoginAsync(
            factory, loginClient, $"569-selected-{Guid.NewGuid():N}@test.local");
        var (legacyFarm, legacyToken, _) = await LoginAsync(
            factory, loginClient, $"569-purged-{Guid.NewGuid():N}@test.local");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Cluckwork.Infrastructure.Persistence.AppDbContext>();
            var purged = await db.RefreshTokens
                .Where(t => t.AccountId == legacyFarm)
                .ExecuteDeleteAsync();
            Assert.True(purged > 0, "the legacy token row must exist before the purge");
        }

        var client = factory.CreateClient(TestHarness.Cookieless(factory));
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        request.Headers.Add(AuthCookies.CsrfHeaderName, "1");
        request.Headers.Add(AuthCookies.ExpectedAccountHeaderName, selectedFarm.ToString());
        request.Headers.Add(
            "Cookie",
            $"{AuthCookies.RefreshCookieNameFor(selectedFarm)}={selectedToken}; "
            + $"{AuthCookies.LegacyRefreshCookieName}={legacyToken}");
        var logout = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        AssertClearsCookie(logout, AuthCookies.RefreshCookieNameFor(selectedFarm));
        AssertNoSetCookie(logout, AuthCookies.LegacyRefreshCookieName);

        var selectedAfterLogout = await client.PostRefreshRawAsync(
            AuthCookies.RefreshCookieNameFor(selectedFarm) + "=" + selectedToken,
            expectedAccount: selectedFarm.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, selectedAfterLogout.StatusCode);
    }

    [Fact]
    public async Task Logout_WithSelectedFarmAndSameFarmLegacyCookie_RevokesBothSessions()
    {
        var email = $"532-same-farm-legacy-logout-{Guid.NewGuid():N}@test.local";
        var loginClient = new TestBrowser(factory);
        var (farm, selectedToken, _) = await LoginAsync(factory, loginClient, email);

        var secondLogin = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new
            {
                farmCode = await factory.FarmCodeForAsync(email),
                email,
                password = TestHarness.Password,
            }),
        };
        var secondLoginResponse = await loginClient.SendAsync(secondLogin);
        secondLoginResponse.EnsureSuccessStatusCode();
        var legacyToken = (await TestHarness.ReadTokensAsync(secondLoginResponse)).RefreshToken;
        var client = factory.CreateClient(TestHarness.Cookieless(factory));

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        request.Headers.Add(AuthCookies.CsrfHeaderName, "1");
        request.Headers.Add(AuthCookies.ExpectedAccountHeaderName, farm.ToString());
        request.Headers.Add(
            "Cookie",
            $"{AuthCookies.RefreshCookieNameFor(farm)}={selectedToken}; "
            + $"{AuthCookies.LegacyRefreshCookieName}={legacyToken}");
        var logout = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        AssertClearsCookie(logout, AuthCookies.RefreshCookieNameFor(farm));
        AssertClearsCookie(logout, AuthCookies.LegacyRefreshCookieName);

        // ORDER IS LOAD-BEARING: probe the LEGACY token first. Presenting the
        // revoked SELECTED token trips #176 reuse detection, which revokes the
        // whole family — including the legacy token. Probed the other way round,
        // this test passes even when logout skips the same-farm legacy revoke
        // entirely, because the second assertion measures that cascade instead
        // of logout. Round 12 proved it: inverting the account comparison left
        // the old ordering green.
        var legacyAfterLogout = await client.PostRefreshRawAsync(
            AuthCookies.LegacyRefreshCookieName + "=" + legacyToken,
            expectedAccount: farm.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, legacyAfterLogout.StatusCode);

        var selectedAfterLogout = await client.PostRefreshRawAsync(
            AuthCookies.RefreshCookieNameFor(farm) + "=" + selectedToken,
            expectedAccount: farm.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, selectedAfterLogout.StatusCode);
    }

    [Fact]
    public async Task LegacyCookie_WithDifferentFarmSelector_IsRefusedAndRotatesNothing()
    {
        var loginClient = new TestBrowser(factory);
        var (farmA, tokenA, _) = await LoginAsync(
            factory, loginClient, $"532-legacy-a-{Guid.NewGuid():N}@test.local");
        var farmB = await factory.SeedAccountWithUserAsync(
            $"532-legacy-b-{Guid.NewGuid():N}@test.local");
        var (familyBefore, tipBefore) = await FamilyAsync(factory, farmA);
        var client = factory.CreateClient(TestHarness.Cookieless(factory));

        var refused = await client.PostRefreshRawAsync(
            AuthCookies.LegacyRefreshCookieName + "=" + tokenA,
            expectedAccount: farmB.ToString());

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal("Auth.SessionChanged", await ProblemTitleAsync(refused));
        Assert.False(refused.Headers.TryGetValues("Set-Cookie", out _));
        var (familyAfter, tipAfter) = await FamilyAsync(factory, farmA);
        Assert.Equal(familyBefore, familyAfter);
        Assert.Equal(tipBefore, tipAfter);

        var stillWorks = await client.PostRefreshRawAsync(
            AuthCookies.LegacyRefreshCookieName + "=" + tokenA);
        Assert.Equal(HttpStatusCode.OK, stillWorks.StatusCode);
    }

    [Fact]
    public async Task UnparseableHeader_IsSessionChanged_AndRotatesNothing()
    {
        var loginClient = new TestBrowser(factory);
        var (farm, token, _) = await LoginAsync(
            factory, loginClient, $"532-m-{Guid.NewGuid():N}@test.local");
        var (familyBefore, tipBefore) = await FamilyAsync(factory, farm);
        var client = factory.CreateClient(TestHarness.Cookieless(factory));

        var refused = await client.PostRefreshRawAsync(
            AuthCookies.RefreshCookieNameFor(farm) + "=" + token,
            expectedAccount: "not-a-guid");

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal("Auth.SessionChanged", await ProblemTitleAsync(refused));
        Assert.False(refused.Headers.TryGetValues("Set-Cookie", out _));
        var (familyAfter, tipAfter) = await FamilyAsync(factory, farm);
        Assert.Equal(familyBefore, familyAfter);
        Assert.Equal(tipBefore, tipAfter);
    }
}
