namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Net.Http.Headers;
using Cluckwork.Api.Endpoints.Auth;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

// #308 — step-up authentication for privileged user administration. A stolen
// but still-valid Owner access token must not be enough, on its own, to (a)
// create another Owner or (b) reset an EXISTING Owner's password: both need a
// fresh step-up grant obtained by re-confirming the CURRENT password
// (POST /auth/step-up). Every other role/target combination (Worker,
// Manager, Sales, ReadOnly — both as the created role and as the reset
// target) stays exactly as #165/#103 left it: UserPasswordTests and
// AdminGatingTests already cover that path working WITHOUT a grant, and
// nothing here changes it — that is the "no blanket prompt" half of the
// acceptance criteria. See StepUpGrantService for the full threat model.
[Collection(IntegrationCollection.Name)]
public sealed class StepUpAuthTests(CluckworkWebApplicationFactory factory)
{
    private sealed record UserRow(Guid Id, string Email, string? DisplayName, string Role);
    private sealed record StepUpDto(string Token, DateTimeOffset ExpiresAt);

    // Runtime-generated and policy-compliant — never a literal secret in source.
    private static string FreshPassword() => $"Aa1!{Guid.NewGuid():N}";

    private async Task<(HttpClient Admin, Guid AccountId)> AdminAsync()
    {
        var email = $"admin-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var admin = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        return (admin, accountId);
    }

    private static async Task<UserRow> FindUserAsync(HttpClient admin, string email)
    {
        var users = await admin.GetFromJsonAsync<List<UserRow>>("/api/v1/users");
        return users!.Single(u => u.Email == email);
    }

    // Seeds a SECOND Owner in the same account (co-owners are equivalent,
    // #165) — the fixture the "reset an Owner's password" tests need.
    private async Task<(string Email, Guid Id)> SeedSecondOwnerAsync(HttpClient admin, Guid accountId)
    {
        var email = $"coowner-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, email, Cluckwork.Domain.Accounts.Roles.Owner);
        return (email, (await FindUserAsync(admin, email)).Id);
    }

    private static async Task<StepUpDto> StepUpAsync(HttpClient client, string password)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/step-up", new { password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<StepUpDto>())!;
    }

    private static Task<HttpResponseMessage> SendWithStepUpAsync(
        HttpClient client, HttpMethod method, string url, object body, string? stepUpToken)
    {
        var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        if (stepUpToken is not null)
            request.Headers.Add(AuthEndpoints.StepUpHeaderName, stepUpToken);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> CreateUserWithStepUpAsync(
        HttpClient client, string email, string role, string? stepUpToken) =>
        SendWithStepUpAsync(client, HttpMethod.Post, "/api/v1/users",
            new { email, password = TestHarness.Password, role }, stepUpToken);

    private static Task<HttpResponseMessage> SetPasswordWithStepUpAsync(
        HttpClient client, Guid userId, string newPassword, string? stepUpToken) =>
        SendWithStepUpAsync(client, HttpMethod.Put, $"/api/v1/users/{userId}/password",
            new { newPassword }, stepUpToken);

    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    // ---------- /auth/step-up itself ----------

    [Fact]
    public async Task StepUp_RequiresAuthentication()
    {
        var response = await factory.CreateClient()
            .PostAsJsonAsync("/api/v1/auth/step-up", new { password = TestHarness.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // #336 review — a wrong step-up password is a CREDENTIAL rejection, not an
    // expired session, and must not be reported with the session-expiry status:
    // the SPA's apiFetch treats every 401 as a stale access token and silently
    // refreshes-and-replays the identical request (see the paired test below for
    // what that costs). 400 is what ChangePassword already returns for the SAME
    // Users.CurrentPasswordIncorrect error — this endpoint now matches it.
    [Fact]
    public async Task StepUp_WrongCurrentPassword_IsACredentialRejection_NotASessionExpiry()
    {
        var (owner, _) = await AdminAsync();
        var response = await owner.PostAsJsonAsync("/api/v1/auth/step-up", new { password = FreshPassword() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Current password is incorrect", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task StepUp_CorrectPassword_ReturnsAnUnexpiredGrant()
    {
        var (owner, _) = await AdminAsync();
        var before = DateTimeOffset.UtcNow;

        var grant = await StepUpAsync(owner, TestHarness.Password);

        Assert.False(string.IsNullOrWhiteSpace(grant.Token));
        Assert.True(grant.ExpiresAt > before);
    }

    [Fact]
    public async Task StepUpToken_CannotBeUsedAsANormalAccessToken()
    {
        var (owner, _) = await AdminAsync();
        var grant = await StepUpAsync(owner, TestHarness.Password);

        var asBearer = factory.CreateAuthedClient(grant.Token);
        var response = await asBearer.GetAsync("/api/v1/users");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------- Accept path: creating another Owner ----------

    [Fact]
    public async Task CreateOwner_WithValidStepUp_Succeeds()
    {
        var (owner, _) = await AdminAsync();
        var grant = await StepUpAsync(owner, TestHarness.Password);
        var newOwnerEmail = $"boss-{Guid.NewGuid():N}@test.local";

        var response = await CreateUserWithStepUpAsync(owner, newOwnerEmail, "Admin", grant.Token);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var users = await owner.GetFromJsonAsync<List<UserRow>>("/api/v1/users");
        Assert.Contains(users!, u => u.Email == newOwnerEmail && u.Role == "Admin");
    }

    [Fact]
    public async Task CreateWorker_NeedsNoStepUp_OrdinaryAdministrationStaysUngated()
    {
        var (owner, _) = await AdminAsync();
        var email = $"hand-{Guid.NewGuid():N}@test.local";

        var response = await CreateUserWithStepUpAsync(owner, email, "Worker", stepUpToken: null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ---------- Reject path: missing / expired / replayed / wrong-account / revoked ----------

    [Fact]
    public async Task CreateOwner_MissingStepUp_Is403_AndNoOwnerCreated()
    {
        var (owner, _) = await AdminAsync();
        var newOwnerEmail = $"boss-{Guid.NewGuid():N}@test.local";

        var response = await CreateUserWithStepUpAsync(owner, newOwnerEmail, "Admin", stepUpToken: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var users = await owner.GetFromJsonAsync<List<UserRow>>("/api/v1/users");
        Assert.DoesNotContain(users!, u => u.Email == newOwnerEmail);
    }

    [Fact]
    public async Task CreateOwner_ExpiredStepUp_Is403_AndNoOwnerCreated()
    {
        var email = $"admin-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        var token = await factory.LoginForAccessTokenAsync(email);

        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var frozen = factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(clock)));
        var owner = frozen.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var grant = await StepUpAsync(owner, TestHarness.Password);
        // Past the default 5-minute grant lifetime (JwtOptions.StepUpGrantMinutes).
        clock.Now = clock.Now.AddMinutes(6);

        var newOwnerEmail = $"boss-{Guid.NewGuid():N}@test.local";
        var response = await CreateUserWithStepUpAsync(owner, newOwnerEmail, "Admin", grant.Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var users = await owner.GetFromJsonAsync<List<UserRow>>("/api/v1/users");
        Assert.DoesNotContain(users!, u => u.Email == newOwnerEmail);
    }

    [Fact]
    public async Task CreateOwner_ReplayedStepUp_SecondUseIs403_AndNoSecondOwnerCreated()
    {
        var (owner, _) = await AdminAsync();
        var grant = await StepUpAsync(owner, TestHarness.Password);

        var firstEmail = $"boss1-{Guid.NewGuid():N}@test.local";
        var first = await CreateUserWithStepUpAsync(owner, firstEmail, "Admin", grant.Token);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var secondEmail = $"boss2-{Guid.NewGuid():N}@test.local";
        var replayed = await CreateUserWithStepUpAsync(owner, secondEmail, "Admin", grant.Token);

        Assert.Equal(HttpStatusCode.Forbidden, replayed.StatusCode);
        var users = await owner.GetFromJsonAsync<List<UserRow>>("/api/v1/users");
        Assert.DoesNotContain(users!, u => u.Email == secondEmail);
    }

    [Fact]
    public async Task CreateOwner_StepUpFromAnotherAccount_Is403_AndNoOwnerCreated()
    {
        var (ownerA, _) = await AdminAsync();
        var grantFromA = await StepUpAsync(ownerA, TestHarness.Password);

        var (ownerB, _) = await AdminAsync();
        var newOwnerEmail = $"boss-{Guid.NewGuid():N}@test.local";

        var response = await CreateUserWithStepUpAsync(ownerB, newOwnerEmail, "Admin", grantFromA.Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var users = await ownerB.GetFromJsonAsync<List<UserRow>>("/api/v1/users");
        Assert.DoesNotContain(users!, u => u.Email == newOwnerEmail);
    }

    [Fact]
    public async Task CreateOwner_StepUpRevokedBySecurityStampChange_Is403_AndNoOwnerCreated()
    {
        var email = $"admin-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        var owner = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        var grant = await StepUpAsync(owner, TestHarness.Password);

        // Rotates the SecurityStamp. The caller's (old) access token stays
        // valid regardless (no server-side denylist — the realistic "stolen
        // token" shape this guard exists for), but the grant embeds the
        // stamp as it was AT ISSUANCE.
        var changed = await owner.PostWithKeyAsync("/api/v1/auth/change-password",
            Guid.NewGuid().ToString(),
            new { currentPassword = TestHarness.Password, newPassword = FreshPassword() });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        var newOwnerEmail = $"boss-{Guid.NewGuid():N}@test.local";
        var response = await CreateUserWithStepUpAsync(owner, newOwnerEmail, "Admin", grant.Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var users = await owner.GetFromJsonAsync<List<UserRow>>("/api/v1/users");
        Assert.DoesNotContain(users!, u => u.Email == newOwnerEmail);
    }

    [Fact]
    public async Task CreateOwner_StepUpRevokedByLogout_Is403_AndNoOwnerCreated()
    {
        var email = $"admin-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        var pair = await factory.LoginAsync(email);
        var owner = factory.CreateAuthedClient(pair.AccessToken);

        var grant = await StepUpAsync(owner, TestHarness.Password);

        var loggedOut = await factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false })
            .PostLogoutAsync(pair.RefreshToken);
        Assert.Equal(HttpStatusCode.NoContent, loggedOut.StatusCode);

        // Same still-valid (unexpired, no denylist) access token as before the
        // logout — the exact "captured before logout, replayed after" shape.
        var newOwnerEmail = $"boss-{Guid.NewGuid():N}@test.local";
        var response = await CreateUserWithStepUpAsync(owner, newOwnerEmail, "Admin", grant.Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var users = await owner.GetFromJsonAsync<List<UserRow>>("/api/v1/users");
        Assert.DoesNotContain(users!, u => u.Email == newOwnerEmail);
    }

    // ---------- Logout revocation identifies the RIGHT user (#336 review) ----------
    //
    // The two credentials a logout can present do not necessarily name the same
    // user. The refresh cookie is per-ORIGIN — a browser holds exactly one and
    // the most recent login owns it — while the SPA keeps each access token in
    // its own TAB's memory (web/src/auth/tokenStore.ts). So a tab still logged
    // in as A survives a later login as B in another tab, and A clicking logout
    // presents B's cookie.
    //
    // Deriving the grant owner from the cookie alone therefore recorded the
    // logout against B and left A's grant usable with A's still-valid (stolen)
    // access token — after A had explicitly logged out. That is exactly the
    // person StepUpGrantService's logout guarantee exists for, so the guarantee
    // silently did not hold for them. Logout now records BOTH the cookie owner
    // and the authenticated bearer's subject.

    // Seeds an independent account + Owner and logs them in. Two of these are two
    // unmistakably different users, so nothing below can pass by conflating them.
    private async Task<(HttpClient Client, TokenPairDto Tokens)> OwnerSessionAsync()
    {
        var email = $"admin-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        var tokens = await factory.LoginAsync(email);
        return (factory.CreateAuthedClient(tokens.AccessToken), tokens);
    }

    // THE FINDING. A holds a grant and logs out from A's own tab — but the
    // browser's single per-origin cookie now belongs to B, so that is what rides
    // along. Recording only the cookie's owner revokes B's grants and leaves A's
    // alive, which is the whole bug.
    [Fact]
    public async Task CreateOwner_StepUpRevokedByLogout_EvenWhenTheCookieBelongsToAnotherUser()
    {
        var (ownerA, tokensA) = await OwnerSessionAsync();
        var (_, tokensB) = await OwnerSessionAsync(); // logged in later — owns the cookie now

        var grant = await StepUpAsync(ownerA, TestHarness.Password);

        // A's tab logs out: A's bearer, B's cookie. Exactly what the browser sends.
        var loggedOut = await factory.CreateClient(Cookieless)
            .PostLogoutAsync(tokensB.RefreshToken, accessToken: tokensA.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, loggedOut.StatusCode);

        // A's access token is untouched by the logout (no server-side denylist) —
        // the realistic "captured before logout, replayed after" shape.
        var newOwnerEmail = $"boss-{Guid.NewGuid():N}@test.local";
        var response = await CreateUserWithStepUpAsync(ownerA, newOwnerEmail, "Admin", grant.Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var users = await ownerA.GetFromJsonAsync<List<UserRow>>("/api/v1/users");
        Assert.DoesNotContain(users!, u => u.Email == newOwnerEmail);
    }

    // …and the cookie owner is still revoked too. The fix ADDS the bearer's
    // subject; it must not quietly swap one user for the other, or an ordinary
    // logout would stop ending the session it was actually given.
    [Fact]
    public async Task Logout_WithAForeignCookie_StillRevokesThatCookiesOwnSession()
    {
        var (_, tokensA) = await OwnerSessionAsync();
        var (_, tokensB) = await OwnerSessionAsync();

        await factory.CreateClient(Cookieless)
            .PostLogoutAsync(tokensB.RefreshToken, accessToken: tokensA.AccessToken);

        // B's refresh token — the credential actually presented — is dead.
        var refreshed = await factory.CreateClient(Cookieless).PostRefreshAsync(tokensB.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, refreshed.StatusCode);
    }

    // The bearer path is deliberately NARROWER than the cookie path: it kills the
    // user's step-up grants without revoking their refresh tokens. Ending one
    // tab's session must not sign A out of every other device, which revoking the
    // whole token family would do. Guards against "fixing" this by over-revoking.
    [Fact]
    public async Task Logout_WithAForeignCookie_DoesNotRevokeTheBearerUsersOtherSessions()
    {
        var (_, tokensA) = await OwnerSessionAsync();
        var (_, tokensB) = await OwnerSessionAsync();

        await factory.CreateClient(Cookieless)
            .PostLogoutAsync(tokensB.RefreshToken, accessToken: tokensA.AccessToken);

        // A's own refresh token was never presented and is still good.
        var refreshed = await factory.CreateClient(Cookieless).PostRefreshAsync(tokensA.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
    }

    // The finding's other named case: no cookie at all (never set, already
    // cleared, or expired). The cookie-only lookup had nothing to key on and
    // recorded nothing, so the grant survived a logout the user really performed.
    [Fact]
    public async Task CreateOwner_StepUpRevokedByLogout_EvenWithNoRefreshCookieAtAll()
    {
        var (owner, tokens) = await OwnerSessionAsync();
        var grant = await StepUpAsync(owner, TestHarness.Password);

        var loggedOut = await factory.CreateClient(Cookieless)
            .PostLogoutAsync(refreshToken: null, accessToken: tokens.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, loggedOut.StatusCode);

        var newOwnerEmail = $"boss-{Guid.NewGuid():N}@test.local";
        var response = await CreateUserWithStepUpAsync(owner, newOwnerEmail, "Admin", grant.Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var users = await owner.GetFromJsonAsync<List<UserRow>>("/api/v1/users");
        Assert.DoesNotContain(users!, u => u.Email == newOwnerEmail);
    }

    // Regression: the ordinary case the SPA now sends — cookie and bearer both
    // present and naming the SAME user. Both paths record a logout for one user;
    // the registry keeps the latest instant per user, so this is idempotent
    // rather than double-counted, and revocation still works.
    [Fact]
    public async Task CreateOwner_StepUpRevokedByLogout_WhenCookieAndBearerAreTheSameUser()
    {
        var (owner, tokens) = await OwnerSessionAsync();
        var grant = await StepUpAsync(owner, TestHarness.Password);

        var loggedOut = await factory.CreateClient(Cookieless)
            .PostLogoutAsync(tokens.RefreshToken, accessToken: tokens.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, loggedOut.StatusCode);

        var newOwnerEmail = $"boss-{Guid.NewGuid():N}@test.local";
        var response = await CreateUserWithStepUpAsync(owner, newOwnerEmail, "Admin", grant.Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // The session ended as well — the cookie path is intact alongside the new one.
        var refreshed = await factory.CreateClient(Cookieless).PostRefreshAsync(tokens.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, refreshed.StatusCode);
    }

    // ---------- Bearer logout survives a cookie-path DB failure (PR #336 review) ----------
    //
    // RevokeRefreshTokenAsync (the cookie path) hits the database and can
    // throw — a transient outage being the realistic case. RecordLogoutAsync
    // (the bearer path) is in-memory and does not. Before AuthEndpoints.Logout
    // recorded the bearer FIRST, a throw from the cookie path skipped the
    // bearer recording entirely: the caller's outstanding step-up grant
    // stayed valid, so a captured access token + grant could still perform
    // the privileged operation after the user had logged out, any time
    // before the grant's own short expiry, if the database recovered first.

    // Decorates the REAL IIdentityProvider so RevokeRefreshTokenAsync throws
    // exactly like a genuine DB outage, while everything else — including
    // RecordLogoutAsync — runs the real EF-backed implementation unchanged.
    // A decorator over a test double for one method, rather than mocking the
    // whole interface, so login/step-up/etc keep exercising production code
    // and only the ONE call this finding is about is faulted.
    private sealed class RevokeRefreshTokenThrowsDecorator(IdentityProvider inner) : IIdentityProvider
    {
        public Task<Result<TokenPair>> LoginAsync(
            string email, string password, CancellationToken ct = default) =>
            inner.LoginAsync(email, password, ct);

        public Task<Result<TokenPair>> RefreshAsync(string refreshToken, CancellationToken ct = default) =>
            inner.RefreshAsync(refreshToken, ct);

        public Task RevokeRefreshTokenAsync(string refreshToken, CancellationToken ct = default) =>
            throw new InvalidOperationException(
                "Simulated transient DB outage (#336 review regression test).");

        public Task RecordLogoutAsync(Guid userId, CancellationToken ct = default) =>
            inner.RecordLogoutAsync(userId, ct);

        public Task<Result<Guid>> CreateUserAsync(
            Guid accountId, string email, string password, string? role,
            string? name = null, CancellationToken ct = default) =>
            inner.CreateUserAsync(accountId, email, password, role, name, ct);

        public Task<Result> UpdateUserAsync(
            Guid accountId, Guid userId, string? name, CancellationToken ct = default) =>
            inner.UpdateUserAsync(accountId, userId, name, ct);

        public Task<Result> SetUserPasswordAsync(
            Guid accountId, Guid userId, string newPassword, CancellationToken ct = default) =>
            inner.SetUserPasswordAsync(accountId, userId, newPassword, ct);

        public Task<Result> BreakGlassResetAsync(
            Guid accountId, Guid userId, string newPassword, string? reason, CancellationToken ct = default) =>
            inner.BreakGlassResetAsync(accountId, userId, newPassword, reason, ct);

        public Task<Result<TokenPair>> ChangeOwnPasswordAsync(
            Guid userId, string currentPassword, string newPassword, CancellationToken ct = default) =>
            inner.ChangeOwnPasswordAsync(userId, currentPassword, newPassword, ct);

        public Task<IReadOnlyList<UserSummary>> ListUsersAsync(Guid accountId, CancellationToken ct = default) =>
            inner.ListUsersAsync(accountId, ct);

        public Task<UserProfile?> GetUserAsync(Guid accountId, Guid userId, CancellationToken ct = default) =>
            inner.GetUserAsync(accountId, userId, ct);

        public Task<Result> SetLanguageAsync(
            Guid accountId, Guid userId, string? language, CancellationToken ct = default) =>
            inner.SetLanguageAsync(accountId, userId, language, ct);
    }

    [Fact]
    public async Task Logout_WhenCookieRevocationThrows_StillRecordsTheBearersLogout_AndFails500()
    {
        var email = $"admin-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        var tokens = await factory.LoginAsync(email);

        // The step-up registry is an in-process singleton (see
        // FrozenClockOwnerAsync above) — grant issuance and the post-logout
        // check must run against the SAME host as the faulted logout call.
        var faulted = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddScoped<IdentityProvider>();
            s.AddScoped<IIdentityProvider>(sp =>
                new RevokeRefreshTokenThrowsDecorator(sp.GetRequiredService<IdentityProvider>()));
        }));

        var owner = faulted.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var grant = await StepUpAsync(owner, TestHarness.Password);

        // Cookie present (so RevokeRefreshTokenAsync is reached and throws)
        // AND the caller authenticated (so the bearer path has a subject to
        // record). Exactly the ordinary logged-in-tab shape.
        var loggedOut = await faulted.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false })
            .PostLogoutAsync(tokens.RefreshToken, accessToken: tokens.AccessToken);

        // The request still fails loudly when the DB is down — worth
        // preserving; the SPA already treats logout as best-effort.
        Assert.Equal(HttpStatusCode.InternalServerError, loggedOut.StatusCode);

        // THE FINDING'S ASSERTION: despite the 500, the bearer's grant is dead.
        var newOwnerEmail = $"boss-{Guid.NewGuid():N}@test.local";
        var response = await CreateUserWithStepUpAsync(owner, newOwnerEmail, "Admin", grant.Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var users = await owner.GetFromJsonAsync<List<UserRow>>("/api/v1/users");
        Assert.DoesNotContain(users!, u => u.Email == newOwnerEmail);
    }

    // The over-revocation guard. Recording a logout must bound the PAST, never
    // poison the user: a grant minted AFTER the logout is a fresh, deliberate
    // re-authentication and has to work, or a single logout would permanently
    // lock the user out of privileged administration.
    [Fact]
    public async Task CreateOwner_StepUpIssuedAfterAnAuthenticatedLogout_IsStillAccepted()
    {
        var (owner, tokens) = await OwnerSessionAsync();

        await factory.CreateClient(Cookieless)
            .PostLogoutAsync(tokens.RefreshToken, accessToken: tokens.AccessToken);

        // A brand-new grant, re-confirming the password after the logout.
        var grant = await StepUpAsync(owner, TestHarness.Password);

        var newOwnerEmail = $"boss-{Guid.NewGuid():N}@test.local";
        var response = await CreateUserWithStepUpAsync(owner, newOwnerEmail, "Admin", grant.Token);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var users = await owner.GetFromJsonAsync<List<UserRow>>("/api/v1/users");
        Assert.Contains(users!, u => u.Email == newOwnerEmail && u.Role == "Admin");
    }

    // An authenticated logout must not start demanding an Idempotency-Key. The
    // bearer resolves a tenant, which is exactly what makes IdempotencyMiddleware
    // require one — /auth/logout is exempt so the SPA can still log out. Without
    // that exemption this is a 400 and logout is broken for every user.
    [Fact]
    public async Task Logout_WithABearer_NeedsNoIdempotencyKey()
    {
        var (_, tokens) = await OwnerSessionAsync();

        var response = await factory.CreateClient(Cookieless)
            .PostLogoutAsync(tokens.RefreshToken, accessToken: tokens.AccessToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------- Logout revocation at sub-second precision (#336 review) ----------

    // The logout instant is recorded with full sub-second ticks, but a JWT's
    // nbf is a NumericDate — whole seconds. Reading issuance off nbf therefore
    // rounded a grant DOWN into (or before) the logout's own second. The three
    // tests below pin both sides of that boundary; see StepUpGrantService's
    // "Revoked by logout" note for why neither flooring the stored logout nor
    // loosening the comparison is an acceptable fix.

    // A whole-second UTC anchor, so the offsets below sit unambiguously inside
    // ONE second and every grant minted between :00.000 and :00.999 shares the
    // same floored nbf.
    private static DateTimeOffset WholeSecondAnchor()
    {
        var now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.UtcTicks - (now.UtcTicks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
    }

    // The logout registry is an in-process singleton, so the logout and the
    // step-up MUST both run against the same host — hence one frozen-clock
    // factory shared by every call in these tests, not the ambient `factory`.
    private async Task<(HttpClient Owner, string RefreshToken, ManualTimeProvider Clock,
        WebApplicationFactory<Program> Host)> FrozenClockOwnerAsync(DateTimeOffset start)
    {
        var email = $"admin-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        var pair = await factory.LoginAsync(email);

        var clock = new ManualTimeProvider(start);
        var frozen = factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(clock)));
        var owner = frozen.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pair.AccessToken);
        return (owner, pair.RefreshToken, clock, frozen);
    }

    private static async Task LogoutAsync(WebApplicationFactory<Program> host, string refreshToken)
    {
        var response = await host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false })
            .PostLogoutAsync(refreshToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // THE BUG. Log out at :00.500, sign back in and take a FRESH grant at
    // :00.800 — genuinely after the logout, but sharing its UTC second. Off
    // nbf the grant reads as issued at :00.000 and is refused for the rest of
    // the second, stranding a user who did nothing wrong.
    [Fact]
    public async Task CreateOwner_StepUpIssuedAfterLogoutInTheSameSecond_IsAccepted()
    {
        var anchor = WholeSecondAnchor();
        var (owner, refreshToken, clock, host) = await FrozenClockOwnerAsync(anchor.AddMilliseconds(500));

        await LogoutAsync(host, refreshToken);

        // Same whole second, later ticks — a grant that did not exist at logout.
        clock.Now = anchor.AddMilliseconds(800);
        var grant = await StepUpAsync(owner, TestHarness.Password);

        var newOwnerEmail = $"boss-{Guid.NewGuid():N}@test.local";
        var response = await CreateUserWithStepUpAsync(owner, newOwnerEmail, "Admin", grant.Token);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var users = await owner.GetFromJsonAsync<List<UserRow>>("/api/v1/users");
        Assert.Contains(users!, u => u.Email == newOwnerEmail && u.Role == "Admin");
    }

    // THE SECURITY PROPERTY, and the reason strictly-before is not an option:
    // a grant minted at :00.200 and a logout at :00.500 land in the same
    // second, and the grant is genuinely EARLIER. It must still be refused.
    [Fact]
    public async Task CreateOwner_StepUpIssuedBeforeLogoutInTheSameSecond_IsStillRevoked()
    {
        var anchor = WholeSecondAnchor();
        var (owner, refreshToken, clock, host) = await FrozenClockOwnerAsync(anchor.AddMilliseconds(200));

        var grant = await StepUpAsync(owner, TestHarness.Password);

        clock.Now = anchor.AddMilliseconds(500);
        await LogoutAsync(host, refreshToken);

        var newOwnerEmail = $"boss-{Guid.NewGuid():N}@test.local";
        var response = await CreateUserWithStepUpAsync(owner, newOwnerEmail, "Admin", grant.Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var users = await owner.GetFromJsonAsync<List<UserRow>>("/api/v1/users");
        Assert.DoesNotContain(users!, u => u.Email == newOwnerEmail);
    }

    // The exact tick where at-or-before and strictly-before disagree: grant and
    // logout at the SAME instant. "At or before" is the documented contract, so
    // this must be refused — the test that goes green if anyone loosens the
    // comparison to `<` while chasing the same-second bug above.
    [Fact]
    public async Task CreateOwner_StepUpIssuedAtTheExactLogoutInstant_IsStillRevoked()
    {
        var (owner, refreshToken, _, host) = await FrozenClockOwnerAsync(WholeSecondAnchor().AddMilliseconds(500));

        var grant = await StepUpAsync(owner, TestHarness.Password);
        // Clock untouched: the logout is recorded at the grant's own instant.
        await LogoutAsync(host, refreshToken);

        var newOwnerEmail = $"boss-{Guid.NewGuid():N}@test.local";
        var response = await CreateUserWithStepUpAsync(owner, newOwnerEmail, "Admin", grant.Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var users = await owner.GetFromJsonAsync<List<UserRow>>("/api/v1/users");
        Assert.DoesNotContain(users!, u => u.Email == newOwnerEmail);
    }

    [Fact]
    public async Task StepUpFailures_AllProduceTheIdenticalNonEnumeratingResponse()
    {
        var (ownerA, _) = await AdminAsync();
        var (ownerB, _) = await AdminAsync();
        var grantFromB = await StepUpAsync(ownerB, TestHarness.Password);

        var missing = await CreateUserWithStepUpAsync(
            ownerA, $"a-{Guid.NewGuid():N}@test.local", "Admin", stepUpToken: null);
        var wrongAccount = await CreateUserWithStepUpAsync(
            ownerA, $"b-{Guid.NewGuid():N}@test.local", "Admin", grantFromB.Token);

        Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
        Assert.Equal(missing.StatusCode, wrongAccount.StatusCode);
        Assert.Equal(await missing.Content.ReadAsStringAsync(), await wrongAccount.Content.ReadAsStringAsync());
    }

    // ---------- Reset an Owner's password ----------

    [Fact]
    public async Task ResetOwnersPassword_WithValidStepUp_Succeeds()
    {
        var (owner, accountId) = await AdminAsync();
        var (coOwnerEmail, coOwnerId) = await SeedSecondOwnerAsync(owner, accountId);
        var grant = await StepUpAsync(owner, TestHarness.Password);
        var newPassword = FreshPassword();

        var response = await SetPasswordWithStepUpAsync(owner, coOwnerId, newPassword, grant.Token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.TryLoginAsync(coOwnerEmail, newPassword)).StatusCode);
    }

    [Fact]
    public async Task ResetOwnersPassword_MissingStepUp_Is403_AndPasswordUnchanged()
    {
        var (owner, accountId) = await AdminAsync();
        var (coOwnerEmail, coOwnerId) = await SeedSecondOwnerAsync(owner, accountId);

        var response = await SetPasswordWithStepUpAsync(owner, coOwnerId, FreshPassword(), stepUpToken: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await factory.TryLoginAsync(coOwnerEmail, TestHarness.Password)).StatusCode);
    }

    [Fact]
    public async Task ResetWorkersPassword_NeedsNoStepUp_OrdinaryAdministrationStaysUngated()
    {
        var (owner, accountId) = await AdminAsync();
        var workerEmail = $"hand-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, workerEmail, asAdmin: false);
        var workerId = (await FindUserAsync(owner, workerEmail)).Id;

        var response = await SetPasswordWithStepUpAsync(owner, workerId, FreshPassword(), stepUpToken: null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // #336 review — /auth/step-up is a SECOND password-verification oracle, and
    // the one guarding Owner takeover. It shipped without the #128 per-account
    // lockout that LoginAsync applies, leaving only the per-IP limiter here —
    // which a distributed attacker rotating source IPs walks around. Guessing
    // past the threshold and then presenting the CORRECT password must still be
    // refused, or the lockout isn't doing anything.
    //
    // Worth recording how this got missed: the original mutation pass reverted
    // 13 existing guards and every one went red. Mutation testing can only
    // falsify guards that EXIST — it is structurally blind to a guard that was
    // never written. Hence a positive test, not another mutant.
    [Fact]
    public async Task StepUp_LocksTheAccountAfterRepeatedWrongPasswords()
    {
        var email = $"admin-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        var admin = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        // Identity's MaxFailedAccessAttempts is 5; go one past it.
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var wrong = await admin.PostAsJsonAsync(
                "/api/v1/auth/step-up", new { password = FreshPassword() });
            Assert.NotEqual(HttpStatusCode.OK, wrong.StatusCode);
        }

        var correct = await admin.PostAsJsonAsync(
            "/api/v1/auth/step-up", new { password = TestHarness.Password });

        Assert.NotEqual(HttpStatusCode.OK, correct.StatusCode);
    }

    // The other half of the boundary: below the threshold the correct password
    // must still work, or the lockout would be a denial-of-service on the
    // legitimate operator who simply mistyped once.
    [Fact]
    public async Task StepUp_BelowTheLockoutThreshold_StillIssuesAGrant()
    {
        var email = $"admin-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        var admin = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        var wrong = await admin.PostAsJsonAsync(
            "/api/v1/auth/step-up", new { password = FreshPassword() });
        Assert.NotEqual(HttpStatusCode.OK, wrong.StatusCode);

        var grant = await StepUpAsync(admin, TestHarness.Password);

        Assert.False(string.IsNullOrWhiteSpace(grant.Token));
    }

    // …and a success must CLEAR the accumulated failures, so a user who mistypes
    // a few times over a long session never drifts into a lockout they can't
    // explain.
    [Fact]
    public async Task StepUp_SuccessResetsTheFailureCount()
    {
        var email = $"admin-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        var admin = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        for (var round = 0; round < 3; round++)
        {
            // Four failures then a success, three times over. Without the reset
            // these twelve failures would cross the threshold of 5.
            for (var attempt = 0; attempt < 4; attempt++)
                await admin.PostAsJsonAsync("/api/v1/auth/step-up", new { password = FreshPassword() });

            var grant = await StepUpAsync(admin, TestHarness.Password);
            Assert.False(string.IsNullOrWhiteSpace(grant.Token));
        }
    }

    // ---------- #336 review: one user attempt must cost ONE failed access ----------
    //
    // web/src/api/client.ts's apiFetch treats EVERY 401 as an expired access
    // token: it silently refreshes the session and REPLAYS the identical
    // request. While /auth/step-up answered a wrong password with 401, one
    // password the operator typed once reached the password check TWICE — so
    // the #128 five-attempt lockout the endpoint gained just above tripped after
    // three submissions, each attempt also burned a refresh-token rotation, and
    // a failed refresh signed the user out mid-flow.
    //
    // The damage is invisible to a bare HttpClient, which issues one request per
    // attempt no matter what the status is — a status-code assertion alone would
    // stay green if the 401 came back. So these two drive the endpoint through a
    // faithful stand-in for the real client instead.
    private static readonly WebApplicationFactoryClientOptions Cookieless =
        new() { HandleCookies = false };

    private sealed class SpaLikeClient(HttpClient http, TokenPairDto tokens)
    {
        public TokenPairDto Tokens { get; private set; } = tokens;

        private Task<HttpResponseMessage> PostAsync(string password)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/step-up")
            {
                Content = JsonContent.Create(new { password })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Tokens.AccessToken);
            return http.SendAsync(request);
        }

        // One user-visible attempt, performed exactly as apiFetch performs it:
        // on a 401 (and ONLY a 401) refresh the session and replay the same
        // password; anything else surfaces to the caller untouched.
        public async Task<HttpStatusCode> StepUpAsync(string password)
        {
            var response = await PostAsync(password);
            if (response.StatusCode != HttpStatusCode.Unauthorized) return response.StatusCode;

            var refreshed = await http.PostRefreshAsync(Tokens.RefreshToken);
            if (!refreshed.IsSuccessStatusCode) return response.StatusCode;
            Tokens = await TestHarness.ReadTokensAsync(refreshed);
            return (await PostAsync(password)).StatusCode;
        }
    }

    private async Task<(string Email, SpaLikeClient Spa)> SpaSessionAsync()
    {
        var email = $"admin-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        var tokens = await factory.LoginAsync(email);
        return (email, new SpaLikeClient(factory.CreateClient(Cookieless), tokens));
    }

    // Read straight off the user row — the lockout's own bookkeeping, not a
    // status code that could agree with the bug by coincidence.
    private async Task<(int FailedCount, bool LockedOut)> LockoutStateAsync(string email)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByEmailAsync(email)
            ?? throw new InvalidOperationException($"No user {email}");
        return (user.AccessFailedCount, await users.IsLockedOutAsync(user));
    }

    [Fact]
    public async Task StepUp_WrongPassword_RecordsExactlyOneFailedAccessPerUserAttempt()
    {
        var (email, spa) = await SpaSessionAsync();
        var originalRefreshToken = spa.Tokens.RefreshToken;

        // Four attempts — one short of the threshold, so nothing here is masked
        // by the account locking part-way through. The COUNT is asserted before
        // the status deliberately: the status is pinned by its own test above,
        // and a mutant that restores the 401 should fail here on the damage it
        // does (Expected 1, Actual 2) rather than on the status that caused it.
        var statuses = new List<HttpStatusCode>();
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            statuses.Add(await spa.StepUpAsync(FreshPassword()));

            // N attempts, N increments — never 2N.
            var (failedCount, _) = await LockoutStateAsync(email);
            Assert.Equal(attempt, failedCount);
        }

        Assert.All(statuses, status => Assert.Equal(HttpStatusCode.BadRequest, status));

        // …and no attempt triggered a transparent refresh, so the session's
        // refresh token was never needlessly rotated either.
        Assert.Equal(originalRefreshToken, spa.Tokens.RefreshToken);
    }

    [Fact]
    public async Task StepUp_LockoutTripsOnTheFifthUserAttempt_NotTheThird()
    {
        var (email, spa) = await SpaSessionAsync();

        for (var attempt = 1; attempt <= 4; attempt++)
            await spa.StepUpAsync(FreshPassword());

        // The third submission is where the doubled count used to lock the
        // account; four real failures are still below the threshold of five.
        var afterFour = await LockoutStateAsync(email);
        Assert.Equal(4, afterFour.FailedCount);
        Assert.False(afterFour.LockedOut);

        // The correct password still works at this point — the operator who
        // mistyped four times is not locked out of their own farm.
        var admin = factory.CreateAuthedClient(spa.Tokens.AccessToken);
        var grant = await StepUpAsync(admin, TestHarness.Password);
        Assert.False(string.IsNullOrWhiteSpace(grant.Token));

        // A success resets the counter, so drive the full five from zero: the
        // FIFTH attempt — not the third — is the one that locks the account.
        for (var attempt = 1; attempt <= 4; attempt++)
            await spa.StepUpAsync(FreshPassword());
        Assert.False((await LockoutStateAsync(email)).LockedOut);

        await spa.StepUpAsync(FreshPassword());
        Assert.True((await LockoutStateAsync(email)).LockedOut);

        // Locked: even the CORRECT password is refused now, with the same reply.
        var afterLockout = await admin.PostAsJsonAsync(
            "/api/v1/auth/step-up", new { password = TestHarness.Password });
        Assert.Equal(HttpStatusCode.BadRequest, afterLockout.StatusCode);
    }
}
