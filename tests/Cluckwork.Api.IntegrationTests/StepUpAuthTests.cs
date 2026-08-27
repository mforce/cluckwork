namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Net.Http.Headers;
using Cluckwork.Api.Endpoints.Auth;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Domain.Catalog;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.SharedState;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

// #308/#360 — step-up authentication for durable user access. A stolen but
// still-valid Owner access token must not be enough, on its own, to create any
// interactive user, reset any user's password, or change any user's role.
// Every role and target needs a fresh step-up grant obtained by re-confirming
// the CURRENT password (POST /auth/step-up). See StepUpGrantService for the
// full threat model.
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
        HttpClient client, HttpMethod method, string url, object? body, string? stepUpToken)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
            request.Content = JsonContent.Create(body);
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

    // ---------- flock scope (#606) ----------

    private sealed record FlockAssignmentRow(Guid Id, Guid? FlockId);
    private sealed record AuditRow(Guid Id, string Action, string? DetailsJson);

    private static Task<HttpResponseMessage> AssignFlockWithStepUpAsync(
        HttpClient client, Guid userId, Guid flockId, string? stepUpToken) =>
        SendWithStepUpAsync(client, HttpMethod.Post, $"/api/v1/users/{userId}/flock-assignments",
            new { flockId }, stepUpToken);

    private static Task<HttpResponseMessage> UnassignFlockWithStepUpAsync(
        HttpClient client, Guid userId, Guid assignmentId, string? stepUpToken) =>
        SendWithStepUpAsync(client, HttpMethod.Delete,
            $"/api/v1/users/{userId}/flock-assignments/{assignmentId}", body: null, stepUpToken);

    private static async Task<List<FlockAssignmentRow>> AssignmentsAsync(HttpClient owner, Guid userId) =>
        (await owner.GetFromJsonAsync<List<FlockAssignmentRow>>(
            $"/api/v1/users/{userId}/flock-assignments"))!;

    private static async Task<int> AuditCountAsync(HttpClient owner, string action, Guid entityId) =>
        (await owner.GetFromJsonAsync<List<AuditRow>>(
            $"/api/v1/audit?action={action}&entityId={entityId}"))!.Count;

    // Runtime Worker/flock fixture: an Owner, an unassigned Worker, and two
    // real flocks in the account, so assignment/unassignment tests exercise a
    // known duplicate pair, a known-but-unassigned pair, and unknown targets.
    private async Task<(HttpClient Owner, Guid WorkerId, Guid FlockA, Guid FlockB)> FlockScopeFixtureAsync()
    {
        var (owner, accountId) = await AdminAsync();
        var farmId = Guid.NewGuid();
        var flockA = await factory.SeedFlockAsync(accountId, farmId);
        var flockB = await factory.SeedFlockAsync(accountId, farmId);
        var workerEmail = $"fw-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, workerEmail, (string?)null);
        var workerId = (await FindUserAsync(owner, workerEmail)).Id;
        return (owner, workerId, flockA, flockB);
    }

    [Fact]
    public async Task AssignFlock_MissingProof_KnownAndUnknownTargetsAreUniformAndWriteNothing()
    {
        var (owner, workerId, flockA, flockB) = await FlockScopeFixtureAsync();

        // Baseline: one real assignment made with its OWN dedicated setup grant.
        var setupGrant = await StepUpAsync(owner, TestHarness.Password);
        var baseline = await AssignFlockWithStepUpAsync(owner, workerId, flockA, setupGrant.Token);
        Assert.Equal(HttpStatusCode.Created, baseline.StatusCode);
        var baselineRows = (await AssignmentsAsync(owner, workerId)).Count;
        var baselineAudits = await AuditCountAsync(owner, AuditActions.UserFlockAssign, workerId);

        // (a) known duplicate pair, missing proof.
        var duplicate = await AssignFlockWithStepUpAsync(owner, workerId, flockA, stepUpToken: null);
        Assert.Equal(HttpStatusCode.Forbidden, duplicate.StatusCode);

        // (b) known Worker, known-but-unassigned flock, missing proof.
        var unassigned = await AssignFlockWithStepUpAsync(owner, workerId, flockB, stepUpToken: null);
        Assert.Equal(HttpStatusCode.Forbidden, unassigned.StatusCode);

        // (c) unknown target ids entirely, missing proof.
        var unknown = await AssignFlockWithStepUpAsync(
            owner, Guid.NewGuid(), Guid.NewGuid(), stepUpToken: null);
        Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);

        // No lookup, no row, no audit event beyond the baseline for any of them.
        Assert.Equal(baselineRows, (await AssignmentsAsync(owner, workerId)).Count);
        Assert.Equal(baselineAudits, await AuditCountAsync(owner, AuditActions.UserFlockAssign, workerId));
    }

    [Fact]
    public async Task UnassignFlock_MissingProof_KnownAndUnknownTargetsAreUniformAndPreserveLastAssignment()
    {
        var (owner, workerId, flockA, _) = await FlockScopeFixtureAsync();

        // One valid fixture assignment, made with its own dedicated setup grant
        // — never reused for the denial attempts below.
        var setupGrant = await StepUpAsync(owner, TestHarness.Password);
        var assign = await AssignFlockWithStepUpAsync(owner, workerId, flockA, setupGrant.Token);
        Assert.Equal(HttpStatusCode.Created, assign.StatusCode);
        var assignmentId = Assert.Single(await AssignmentsAsync(owner, workerId)).Id;

        // Real route pair, missing proof.
        var real = await UnassignFlockWithStepUpAsync(owner, workerId, assignmentId, stepUpToken: null);
        Assert.Equal(HttpStatusCode.Forbidden, real.StatusCode);

        // Unknown assignment id under the same worker, missing proof.
        var unknown = await UnassignFlockWithStepUpAsync(
            owner, workerId, Guid.NewGuid(), stepUpToken: null);
        Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);

        // The only assignment row remains, and no unassignment audit was written.
        Assert.Single(await AssignmentsAsync(owner, workerId));
        Assert.Equal(0, await AuditCountAsync(owner, AuditActions.UserFlockUnassign, workerId));
    }

    [Fact]
    public async Task FlockScope_FreshProofPerMutation_AssignsThenRemovesLastAssignment()
    {
        var (owner, workerId, flockA, _) = await FlockScopeFixtureAsync();
        Assert.Empty(await AssignmentsAsync(owner, workerId));

        var assignGrant = await StepUpAsync(owner, TestHarness.Password);
        var assign = await AssignFlockWithStepUpAsync(owner, workerId, flockA, assignGrant.Token);
        Assert.Equal(HttpStatusCode.Created, assign.StatusCode);
        var assignmentId = Assert.Single(await AssignmentsAsync(owner, workerId)).Id;

        var removeGrant = await StepUpAsync(owner, TestHarness.Password);
        var remove = await UnassignFlockWithStepUpAsync(owner, workerId, assignmentId, removeGrant.Token);
        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);
        Assert.Empty(await AssignmentsAsync(owner, workerId));
    }

    [Fact]
    public async Task FlockScope_OneGrantCannotAuthorizeAssignmentThenUnassignment()
    {
        var (owner, workerId, flockA, _) = await FlockScopeFixtureAsync();

        var grant = await StepUpAsync(owner, TestHarness.Password);
        var assign = await AssignFlockWithStepUpAsync(owner, workerId, flockA, grant.Token);
        Assert.Equal(HttpStatusCode.Created, assign.StatusCode);
        var assignmentId = Assert.Single(await AssignmentsAsync(owner, workerId)).Id;

        // Reusing the SAME (already-consumed) grant for the unassignment.
        var reuse = await UnassignFlockWithStepUpAsync(owner, workerId, assignmentId, grant.Token);
        Assert.Equal(HttpStatusCode.Forbidden, reuse.StatusCode);
        Assert.Single(await AssignmentsAsync(owner, workerId));
    }

    [Fact]
    public async Task AssignFlock_ValidProofConflictConsumesGrant()
    {
        var (owner, workerId, flockA, flockB) = await FlockScopeFixtureAsync();
        var setupGrant = await StepUpAsync(owner, TestHarness.Password);
        var baseline = await AssignFlockWithStepUpAsync(owner, workerId, flockA, setupGrant.Token);
        Assert.Equal(HttpStatusCode.Created, baseline.StatusCode);

        // A fresh proof on a DUPLICATE assignment: 409, and the proof is spent.
        var grant = await StepUpAsync(owner, TestHarness.Password);
        var duplicate = await AssignFlockWithStepUpAsync(owner, workerId, flockA, grant.Token);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        // Reusing that same (spent) proof on an otherwise-valid assignment: 403.
        var reused = await AssignFlockWithStepUpAsync(owner, workerId, flockB, grant.Token);
        Assert.Equal(HttpStatusCode.Forbidden, reused.StatusCode);

        // Neither the duplicate nor the reused attempt produced a row or audit event.
        Assert.Single(await AssignmentsAsync(owner, workerId));
        Assert.Equal(1, await AuditCountAsync(owner, AuditActions.UserFlockAssign, workerId));
    }

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
    public async Task CreateWorker_MissingStepUp_Is403_AndNoUserCreated()
    {
        var (owner, _) = await AdminAsync();
        var email = $"hand-{Guid.NewGuid():N}@test.local";

        var response = await CreateUserWithStepUpAsync(owner, email, "Worker", stepUpToken: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var users = await owner.GetFromJsonAsync<List<UserRow>>("/api/v1/users");
        Assert.DoesNotContain(users!, u => u.Email == email);
    }

    [Fact]
    public async Task CreateWorker_WithValidStepUp_Succeeds()
    {
        var (owner, _) = await AdminAsync();
        var grant = await StepUpAsync(owner, TestHarness.Password);
        var email = $"hand-{Guid.NewGuid():N}@test.local";

        var response = await CreateUserWithStepUpAsync(owner, email, "Worker", grant.Token);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var users = await owner.GetFromJsonAsync<List<UserRow>>("/api/v1/users");
        Assert.Contains(users!, u => u.Email == email && u.Role == "Worker");
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

        // Rotates the SecurityStamp. JWT validation never checks SecurityStamp
        // (it isn't embedded in the access token), so the token used for THIS
        // call keeps authenticating it regardless — the realistic "stolen
        // token" shape this guard exists for. Since #364 this same
        // change-password call also bumps CredentialEpoch, so a captured OLD
        // token would fail CredentialEpochMiddleware on its next request
        // either way; the grant is what closes the narrower gap before that,
        // since it separately embeds and checks the stamp AT ISSUANCE.
        var changed = await owner.PostWithKeyAsync("/api/v1/auth/change-password",
            Guid.NewGuid().ToString(),
            new { currentPassword = TestHarness.Password, newPassword = FreshPassword() });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var ownerAfterPasswordChange = factory.CreateAuthedClient(
            (await TestHarness.ReadTokensAsync(changed)).AccessToken);

        var newOwnerEmail = $"boss-{Guid.NewGuid():N}@test.local";
        var response = await CreateUserWithStepUpAsync(ownerAfterPasswordChange, newOwnerEmail, "Admin", grant.Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var users = await ownerAfterPasswordChange.GetFromJsonAsync<List<UserRow>>("/api/v1/users");
        Assert.DoesNotContain(users!, u => u.Email == newOwnerEmail);
    }

    [Fact]
    public async Task CreateOwner_StepUpRevokedByLogout_Is403_AndNoOwnerCreated()
    {
        var email = $"admin-{Guid.NewGuid():N}@test.local";
        var account = await factory.SeedAccountWithUserAsync(email);
        var pair = await factory.LoginAsync(email);
        var owner = factory.CreateAuthedClient(pair.AccessToken);

        var grant = await StepUpAsync(owner, TestHarness.Password);

        var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        logoutRequest.Headers.Add(AuthCookies.CsrfHeaderName, "1");
        logoutRequest.Headers.Add("Cookie", AuthCookies.RefreshCookieNameFor(account) + "=" + pair.RefreshToken);
        logoutRequest.Headers.Add(AuthCookies.ExpectedAccountHeaderName, account.ToString());
        var loggedOut = await factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false })
            .SendAsync(logoutRequest);
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
    private async Task<(HttpClient Client, Guid Account, TokenPairDto Tokens)> OwnerSessionAsync()
    {
        var email = $"admin-{Guid.NewGuid():N}@test.local";
        var account = await factory.SeedAccountWithUserAsync(email);
        var tokens = await factory.LoginAsync(email);
        // #532 — the cookie the logout/refresh endpoints read is the one the
        // token's farm owns, and the token's farm is exactly the seed's: the
        // access token's account_id claim is minted from the user row this
        // seed created. (The earlier version of this helper re-derived the
        // account from the JWT — an extra moving part for no gain.)
        return (factory.CreateAuthedClient(tokens.AccessToken), account, tokens);
    }

    // THE FINDING. A holds a grant and logs out while presenting B's selected
    // per-farm cookie. Recording only the cookie's owner revokes B's grants and
    // leaves A's alive, which is the whole bug.
    [Fact]
    public async Task CreateOwner_StepUpRevokedByLogout_EvenWhenTheCookieBelongsToAnotherUser()
    {
        var (ownerA, _, tokensA) = await OwnerSessionAsync();
        var (_, accountB, tokensB) = await OwnerSessionAsync();

        var grant = await StepUpAsync(ownerA, TestHarness.Password);

        // A's tab logs out: A's bearer, B's cookie. Exactly what the browser sends.
        // #532 — the browser sends every farm's cookie; the tab declares which
        // one it means, so the presented token rides B's per-farm name.
        var loggedOut = await factory.CreateClient(Cookieless)
            .PostLogoutAsync(tokensB.RefreshToken, accessToken: tokensA.AccessToken, accountId: accountB);
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
        var (_, accountA, tokensA) = await OwnerSessionAsync();
        var (_, accountB, tokensB) = await OwnerSessionAsync();

        // #532 — the pre-per-farm logout read the SHARED cookie, so whatever
        // cookie value a request carried was what the endpoint hashed. With
        // per-farm cookies the endpoint reads the NAMED farm's cookie: the
        // presented value rides under B's per-farm name (the tab is B's tab),
        // and what gets revoked is the value the browser actually holds.
        var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        logoutRequest.Headers.Add(AuthCookies.CsrfHeaderName, "1");
        logoutRequest.Headers.Add("Cookie", AuthCookies.RefreshCookieNameFor(accountB) + "=" + tokensB.RefreshToken);
        logoutRequest.Headers.Add(AuthCookies.ExpectedAccountHeaderName, accountB.ToString());
        logoutRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokensA.AccessToken);
        await factory.CreateClient(Cookieless).SendAsync(logoutRequest);

        // B's refresh token — the credential actually presented — is dead.
        var refreshed = await factory.CreateClient(Cookieless)
            .PostRefreshAsync(tokensB.RefreshToken, csrf: true, expectedAccount: accountB.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, refreshed.StatusCode);
    }

    // The bearer path is deliberately NARROWER than the cookie path: it kills the
    // user's step-up grants without revoking their refresh tokens. Ending one
    // tab's session must not sign A out of every other device, which revoking the
    // whole token family would do. Guards against "fixing" this by over-revoking.
    [Fact]
    public async Task Logout_WithAForeignCookie_DoesNotRevokeTheBearerUsersOtherSessions()
    {
        var (_, accountA, tokensA) = await OwnerSessionAsync();
        var (_, accountB, tokensB) = await OwnerSessionAsync();

        var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        logoutRequest.Headers.Add(AuthCookies.CsrfHeaderName, "1");
        logoutRequest.Headers.Add("Cookie", AuthCookies.RefreshCookieNameFor(accountB) + "=" + tokensB.RefreshToken);
        logoutRequest.Headers.Add(AuthCookies.ExpectedAccountHeaderName, accountB.ToString());
        logoutRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokensA.AccessToken);
        await factory.CreateClient(Cookieless).SendAsync(logoutRequest);

        // A's own refresh token was never presented and is still good.
        var refreshed = await factory.CreateClient(Cookieless)
            .PostRefreshRawAsync(AuthCookies.RefreshCookieNameFor(accountA) + "=" + tokensA.RefreshToken, expectedAccount: accountA.ToString());
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
    }

    // The finding's other named case: no cookie at all (never set, already
    // cleared, or expired). The cookie-only lookup had nothing to key on and
    // recorded nothing, so the grant survived a logout the user really performed.
    [Fact]
    public async Task CreateOwner_StepUpRevokedByLogout_EvenWithNoRefreshCookieAtAll()
    {
        var (owner, account, tokens) = await OwnerSessionAsync();
        var grant = await StepUpAsync(owner, TestHarness.Password);

        var loggedOut = await factory.CreateClient(Cookieless)
            .PostLogoutAsync(refreshToken: null, accessToken: tokens.AccessToken, accountId: account);
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
        var (owner, account, tokens) = await OwnerSessionAsync();
        var grant = await StepUpAsync(owner, TestHarness.Password);

        var loggedOut = await factory.CreateClient(Cookieless)
            .PostLogoutAsync(tokens.RefreshToken, accessToken: tokens.AccessToken, accountId: account);
        Assert.Equal(HttpStatusCode.NoContent, loggedOut.StatusCode);

        var newOwnerEmail = $"boss-{Guid.NewGuid():N}@test.local";
        var response = await CreateUserWithStepUpAsync(owner, newOwnerEmail, "Admin", grant.Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // The session ended as well — the cookie path is intact alongside the new one.
        var refreshed = await factory.CreateClient(Cookieless)
            .PostRefreshAsync(tokens.RefreshToken, csrf: true, expectedAccount: account.ToString());
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
            Guid accountId, string email, string password, CancellationToken ct = default) =>
            inner.LoginAsync(accountId, email, password, ct);

        public Task<Result<TokenPair>> RefreshAsync(string refreshToken, CancellationToken ct = default, Guid? expectedAccountId = null) =>
            inner.RefreshAsync(refreshToken, ct, expectedAccountId);

        public Task RevokeRefreshTokenAsync(
            string refreshToken, CancellationToken ct = default, Guid? expectedAccountId = null) =>
            throw new InvalidOperationException(
                "Simulated transient DB outage (#336 review regression test).");

        public Task RecordLogoutAsync(Guid userId, CancellationToken ct = default) =>
            inner.RecordLogoutAsync(userId, ct);

        public Task<Result<Guid>> CreateUserAsync(
            Guid accountId, string email, string password, string? role,
            string? name = null, bool mustChangePassword = false, CancellationToken ct = default) =>
            inner.CreateUserAsync(accountId, email, password, role, name, mustChangePassword, ct);

        public Task<Result> UpdateUserAsync(
            Guid accountId, Guid userId, string? name, CancellationToken ct = default) =>
            inner.UpdateUserAsync(accountId, userId, name, ct);

        public Task<Result> SetUserPasswordAsync(
            Guid accountId, Guid userId, string newPassword, CancellationToken ct = default) =>
            inner.SetUserPasswordAsync(accountId, userId, newPassword, ct);

        public Task<Result> ChangeUserRoleAsync(
            Guid accountId, Guid userId, string? role, Guid actingUserId, CancellationToken ct = default) =>
            inner.ChangeUserRoleAsync(accountId, userId, role, actingUserId, ct);

        public Task<Result> ChangeUserEmailAsync(
            Guid accountId, Guid userId, string email, Guid actingUserId, CancellationToken ct = default) =>
            inner.ChangeUserEmailAsync(accountId, userId, email, actingUserId, ct);

        public Task<Result> DisableUserAsync(
            Guid accountId, Guid userId, Guid actingUserId, string? reason, CancellationToken ct = default) =>
            inner.DisableUserAsync(accountId, userId, actingUserId, reason, ct);

        public Task<Result> EnableUserAsync(
            Guid accountId, Guid userId, Guid actingUserId, CancellationToken ct = default) =>
            inner.EnableUserAsync(accountId, userId, actingUserId, ct);

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

        public Task<Result> SetStepperUnitAsync(
            Guid accountId, Guid userId, EggUnit? unit, CancellationToken ct = default) =>
            inner.SetStepperUnitAsync(accountId, userId, unit, ct);
    }

    [Fact]
    public async Task Logout_WhenCookieRevocationThrows_StillRecordsTheBearersLogout_AndFails500()
    {
        var email = $"admin-{Guid.NewGuid():N}@test.local";
        var account = await factory.SeedAccountWithUserAsync(email);
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
            .PostLogoutAsync(tokens.RefreshToken, accessToken: tokens.AccessToken, accountId: account);

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

    // Throws only on the bulk revoke UPDATE inside RevokeRefreshTokenAsync, so
    // the owner LOOKUP immediately before it still succeeds. A decorator over
    // IIdentityProvider cannot express this — it would replace the whole method
    // and skip the lookup too, which is exactly the half that must still run.
    private sealed class RevokeUpdateFaultInterceptor : DbCommandInterceptor
    {
        public volatile bool Armed;

        private void MaybeFail(System.Data.Common.DbCommand command)
        {
            // The RefreshToken entity maps to the explicitly snake_cased table
            // "refresh_tokens" (RefreshToken.cs: builder.ToTable("refresh_tokens")) —
            // not the CLR type name. Matching "RefreshTokens" here was a silent
            // no-op: it never matched the real generated SQL, so the interceptor
            // never fired regardless of how it was wired into the host.
            if (Armed
                && command.CommandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("refresh_tokens", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Simulated refresh-token revoke failure (test fault injection).");
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            MaybeFail(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, CommandEventData eventData,
            InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            MaybeFail(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    // #336 review (2nd round) — the cookie path's own ordering, one layer below
    // Logout_WhenCookieRevocationThrows_... above. That test proves the BEARER
    // is recorded when RevokeRefreshTokenAsync throws wholesale; this one proves
    // the COOKIE OWNER is recorded when the failure happens INSIDE it, after the
    // owner lookup succeeded but during the bulk UPDATE.
    //
    // Exercises IdentityProvider.RevokeRefreshTokenAsync directly rather than
    // through the HTTP endpoint. Two DI-based ways to fault ONLY the bulk UPDATE
    // from outside the host were tried first and both fail silently: a second
    // AddDbContext(...) never attaches (DbContextOptions is registered with
    // TryAdd semantics, so it's a no-op) and AddSingleton<IInterceptor>(...)
    // alone never fires against the host's own context either. Building the
    // provider's other dependencies off the real host (real UserManager /
    // RoleManager / JwtTokenService / JwtOptions, so login/step-up plumbing
    // stays production code) but constructing the AppDbContext directly with
    // the interceptor attached — same pattern as ReportQueryBoundingTests —
    // reaches the exact statement. Asserting on a privately-held
    // IStepUpGrantRegistry, rather than a second HTTP round trip through a
    // step-up grant, is also the more direct guarantee: the grant is dead,
    // not "some request somewhere came back 403".
    [Fact]
    public async Task RevokeRefreshTokenAsync_WhenTheBulkUpdateThrows_StillRecordsTheOwnersLogout()
    {
        var email = $"admin-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var tokens = await factory.LoginAsync(email);

        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByEmailAsync(email);
        Assert.NotNull(user);

        var interceptor = new RevokeUpdateFaultInterceptor();
        var tenant = new TenantContext();
        tenant.Resolve(accountId);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(factory.ConnectionString)
            .AddInterceptors(interceptor)
            .Options;
        await using var db = new AppDbContext(options, tenant, new FlockScope());

        // A registry we hold a direct reference to — not the host's DI registration
        // — so the assertion below reads the exact call this provider made,
        // rather than going back through HTTP.
        var timeProvider = services.GetRequiredService<TimeProvider>();
        var registry = new PersistentStepUpGrantRegistry(
            new InProcessClaimOnceStore(timeProvider), db);
        var provider = new IdentityProvider(
            userManager,
            services.GetRequiredService<RoleManager<ApplicationRole>>(),
            services.GetRequiredService<IJwtTokenService>(),
            db,
            services.GetRequiredService<IOptions<JwtOptions>>(),
            timeProvider,
            services.GetRequiredService<IAuditWriter>(),
            registry,
            services.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>(),
            services.GetRequiredService<AuthSecurityEventLogger>(),
            services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<IdentityProvider>>(),
            services.GetRequiredService<Cluckwork.Application.Features.Accounts.IAccountRepository>(),
            new AccountUserDirectory(db, services.GetRequiredService<ILookupNormalizer>()));

        var beforeRevoke = timeProvider.GetUtcNow();

        // TestHarness.ExtractRefreshCookie reads the raw Set-Cookie header text,
        // which ASP.NET Core percent-encodes (the trailing base64 "=" comes back
        // as "%3D"). Every other helper here calls back over HTTP, where the
        // framework's own Cookie-header parsing decodes it again before the
        // endpoint ever sees it — a round trip this direct, in-process call
        // skips, so it must undo the encoding itself or hash a value the login
        // call never actually stored.
        var rawRefreshToken = tokens.RefreshTokenForDirectCall;

        interceptor.Armed = true;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.RevokeRefreshTokenAsync(rawRefreshToken));
        interceptor.Armed = false;

        // THE FINDING'S ASSERTION: the owner lookup (before the faulted UPDATE)
        // still ran, and its result was recorded despite the throw.
        Assert.True(await registry.IsRevokedByLogoutAsync(user!.Id, 0));
    }

    // ---------- The admission decision is ONE atomic registry call (#336 review, 3rd round) ----------
    //
    // ValidateAsync used to end with TWO separate registry calls:
    //
    //     if (registry.IsRevokedByLogout(userId, issuedAt)) return denied;
    //     if (!registry.TryConsume(jti, expiresAt, now))    return denied;
    //
    // Each was individually atomic — a ConcurrentDictionary apiece — but the
    // PAIR was not, and the registry is a process-wide singleton shared by
    // every concurrent request. A logout completing in the window between the
    // two lines is invisible to a validation already past the first one:
    //
    //     T1 (validate): IsRevokedByLogout -> false   (no logout recorded yet)
    //     T2 (logout):   RecordLogout(userId, now)
    //     T1 (validate): TryConsume(jti, ...) -> true -> Success
    //
    // …and the privileged call proceeds on a grant minted BEFORE a logout that
    // has already completed — creating another Owner, or resetting an Owner's
    // password. That is exactly the guarantee the logout-revocation bullet in
    // StepUpGrantService's threat model claims to provide, and it failed OPEN.
    //
    // Guarding it with a timing test would be guarding it with luck: the window
    // is microseconds wide and a stress test that happens not to hit it passes
    // just as green as a correct implementation. So the guard is STRUCTURAL —
    // the decision must be reachable in exactly one registry call, which is a
    // property of the code and not of the scheduler. A spy registry records
    // every call ValidateAsync makes; if anyone re-splits the decision, the
    // reintroduced IsRevokedByLogout call shows up here immediately and
    // deterministically. (The semantics of the combined operation, and the
    // fact that RecordLogout serialises against it, are pinned separately in
    // StepUpGrantRegistryTests; that the fused decision still REVOKES over
    // HTTP — so nobody can satisfy the spy by dropping the logout half — is
    // already pinned by CreateOwner_StepUpRevokedByLogout_* above.)
    //
    // Wrapping the real persistent registry rather than faking the answers,
    // so the accept/replay outcomes asserted below are the production ones and
    // the test cannot go green against a registry that does nothing.
    private sealed class RecordingStepUpGrantRegistry(
        IClaimOnceStore claimOnce, AppDbContext db) : IStepUpGrantRegistry
    {
        private readonly PersistentStepUpGrantRegistry inner = new(claimOnce, db);

        // Ordered log of every interface member invoked. Not a counter: the
        // ORDER and the exact membership are both part of what is asserted.
        public List<string> Calls { get; } = [];

        public Task<bool> TryConsumeIfNotLoggedOutAsync(
            Guid userId, Guid jti, int grantEpoch, DateTimeOffset expiresAt,
            DateTimeOffset now, CancellationToken ct = default)
        {
            Calls.Add(nameof(TryConsumeIfNotLoggedOutAsync));
            return inner.TryConsumeIfNotLoggedOutAsync(userId, jti, grantEpoch, expiresAt, now, ct);
        }

        public Task RecordLogoutAsync(Guid userId, CancellationToken ct = default)
        {
            Calls.Add(nameof(RecordLogoutAsync));
            return inner.RecordLogoutAsync(userId, ct);
        }

        public Task<bool> IsRevokedByLogoutAsync(
            Guid userId, int grantEpoch, CancellationToken ct = default)
        {
            Calls.Add(nameof(IsRevokedByLogoutAsync));
            return inner.IsRevokedByLogoutAsync(userId, grantEpoch, ct);
        }
    }

    // Builds a StepUpGrantService directly off the real host — the same pattern
    // as RevokeRefreshTokenAsync_WhenTheBulkUpdateThrows_... above, so every
    // dependency except the registry is production code (real UserManager, real
    // JwtOptions/keys, real clock) and only the collaborator under observation
    // is substituted. Over HTTP the host's own registry registration would be
    // in play instead and its calls could not be attributed to one request.
    //
    // The caller owns the returned AppDbContext (await using) and the scope.
    private async Task<(StepUpGrantService StepUp, AppDbContext Db, Guid AccountId, Guid UserId)>
        DirectStepUpServiceAsync(IServiceScope scope, IStepUpGrantRegistry registry)
    {
        var email = $"admin-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);

        var services = scope.ServiceProvider;
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByEmailAsync(email);
        Assert.NotNull(user);

        var tenant = new TenantContext();
        tenant.Resolve(accountId);
        var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(factory.ConnectionString).Options,
            tenant, new FlockScope());

        var stepUp = new StepUpGrantService(
            userManager,
            db,
            services.GetRequiredService<IOptions<JwtOptions>>(),
            services.GetRequiredService<TimeProvider>(),
            registry,
            services.GetRequiredService<AuthSecurityEventLogger>());

        return (stepUp, db, accountId, user!.Id);
    }

    [Fact]
    public async Task IssueAsync_DirectlyRefusesADisabledUser()
    {
        using var scope = factory.Services.CreateScope();
        var (stepUp, db, accountId, userId) = await DirectStepUpServiceAsync(scope, null!);
        await using var _ = db;
        var services = scope.ServiceProvider;
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        // Rebuild over the helper's real db now that it exists — the helper's
        // placeholder registry was never invoked by IssueAsync.
        stepUp = new StepUpGrantService(
            userManager, db,
            services.GetRequiredService<IOptions<JwtOptions>>(),
            services.GetRequiredService<TimeProvider>(),
            new PersistentStepUpGrantRegistry(
                new InProcessClaimOnceStore(services.GetRequiredService<TimeProvider>()),
                db),
            services.GetRequiredService<AuthSecurityEventLogger>());
        var user = await userManager.FindByIdAsync(userId.ToString());
        Assert.NotNull(user);
        user.DisabledAt = DateTimeOffset.UtcNow;
        var disabled = await userManager.UpdateAsync(user);
        Assert.True(disabled.Succeeded);

        var result = await stepUp.IssueAsync(accountId, userId, TestHarness.Password);

        Assert.True(result.IsFailure);
        Assert.Equal("Users.CurrentPasswordIncorrect", result.Error.Code);
    }

    [Fact]
    public async Task ValidateAsync_MakesTheAdmissionDecisionInOneAtomicRegistryCall()
    {
        using var scope = factory.Services.CreateScope();
        var (stepUp, db, accountId, userId) = await DirectStepUpServiceAsync(scope, null!);
        await using var _ = db;
        var timeProvider = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var spy = new RecordingStepUpGrantRegistry(
            new InProcessClaimOnceStore(timeProvider), db);
        // Rebuild the service over the same db with the spy in place: the
        // registry is a constructor dependency, and every other collaborator is
        // the same production instance the helper already resolved.
        stepUp = new StepUpGrantService(
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(), db,
            scope.ServiceProvider.GetRequiredService<IOptions<JwtOptions>>(),
            timeProvider, spy,
            scope.ServiceProvider.GetRequiredService<AuthSecurityEventLogger>());

        var issued = await stepUp.IssueAsync(accountId, userId, TestHarness.Password);
        Assert.True(issued.IsSuccess);

        // Issuance is registry-free: a grant is only ever recorded when it is
        // USED, so an unused grant costs nothing and expiry alone retires it.
        Assert.Empty(spy.Calls);

        var accepted = await stepUp.ValidateAsync(accountId, userId, issued.Value.Token);

        // Real production outcome, not a stubbed one — the spy delegates.
        Assert.True(accepted.IsSuccess);

        // THE FINDING'S ASSERTION. The logout epoch is no longer consulted
        // separately: reading it and consuming the jti are one indivisible
        // decision, so a concurrent RecordLogout cannot land between them.
        // Asserted first, and by name, so re-splitting the decision fails here
        // with the culprit spelled out rather than as a sequence mismatch.
        Assert.DoesNotContain(nameof(IStepUpGrantRegistry.IsRevokedByLogoutAsync), spy.Calls);
        Assert.Equal(
            new[] { nameof(IStepUpGrantRegistry.TryConsumeIfNotLoggedOutAsync) },
            spy.Calls);

        // The DENIAL path takes the same single call — a replay must not start
        // consulting the epoch separately either. This also asserts the replay
        // is refused BY THE REGISTRY: the second call reaches it at all (rather
        // than dying earlier on a spurious signature failure) only because of
        // the CacheSignatureProviders fix in ValidateAsync — see the comment
        // there, and RepeatedValidations_... below.
        var replayed = await stepUp.ValidateAsync(accountId, userId, issued.Value.Token);

        Assert.True(replayed.IsFailure);
        Assert.DoesNotContain(nameof(IStepUpGrantRegistry.IsRevokedByLogoutAsync), spy.Calls);
        Assert.Equal(
            new[]
            {
                nameof(IStepUpGrantRegistry.TryConsumeIfNotLoggedOutAsync),
                nameof(IStepUpGrantRegistry.TryConsumeIfNotLoggedOutAsync),
            },
            spy.Calls);
    }

    [Fact]
    public async Task ValidateAsync_ReadOnlySecurityStampLookup_DoesNotTrackTheUser()
    {
        var email = $"admin-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var userId = await factory.WithTenantScopeAsync(accountId,
            db => db.Users.Where(user => user.Email == email).Select(user => user.Id).SingleAsync());
        using var scope = factory.Services.CreateScope();
        scope.ResolveTenantAndActor(accountId, userId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stepUp = scope.ServiceProvider.GetRequiredService<IStepUpGrantService>();
        var issued = await stepUp.IssueAsync(accountId, userId, TestHarness.Password);
        Assert.True(issued.IsSuccess);
        db.ChangeTracker.Clear();

        var validated = await stepUp.ValidateAsync(accountId, userId, issued.Value.Token);

        Assert.True(validated.IsSuccess);
        Assert.DoesNotContain(db.ChangeTracker.Entries<ApplicationUser>(),
            entry => entry.Entity.Id == userId);
    }

    // A SEPARATE defect, found while writing the test above and fixed in the
    // same change because it was masking that test's replay assertion.
    //
    // ValidateAsync builds its RsaSecurityKey over a `using var rsa` it disposes
    // on return, while the DEFAULT CryptoProviderFactory caches the verifying
    // SignatureProvider process-wide, keyed on the KEY MATERIAL — which is
    // identical on every call, since it is the same configured public key. Two
    // validations close enough together therefore gave the second one a cached
    // provider holding the first call's DISPOSED RSA, and it failed with
    // SecurityTokenSignatureKeyNotFoundException: a perfectly good grant refused
    // because an unrelated grant happened to be validated moments earlier.
    // IssueAsync already sets CacheSignatureProviders = false on the SIGNING
    // side for the same reason; the verifying side did not.
    //
    // It fails CLOSED, so it was never a security hole — but it is a real,
    // timing-dependent denial of a legitimate step-up, and worse for coverage:
    // CreateOwner_ReplayedStepUp_SecondUseIs403 asserts only a 403, which this
    // spurious signature failure produces just as readily as the registry's
    // replay refusal does. The single-use guarantee's end-to-end test could
    // therefore pass without the single-use guard doing anything.
    //
    // TWO DIFFERENT grants, back to back on one service: both must validate.
    // Distinct grants (not a replay) so the registry cannot be what refuses the
    // second one, and back-to-back deliberately — that is the window in which
    // the stale cache entry is still live.
    [Fact]
    public async Task RepeatedValidations_OfDistinctGrants_AllSucceed_DespiteTheSharedSigningKey()
    {
        using var scope = factory.Services.CreateScope();
        var (stepUp, db, accountId, userId) = await DirectStepUpServiceAsync(scope, null!);
        await using var _ = db;
        var repeatedServices = scope.ServiceProvider;
        var repeatedTp = repeatedServices.GetRequiredService<TimeProvider>();
        stepUp = new StepUpGrantService(
            repeatedServices.GetRequiredService<UserManager<ApplicationUser>>(), db,
            repeatedServices.GetRequiredService<IOptions<JwtOptions>>(),
            repeatedTp,
            new PersistentStepUpGrantRegistry(new InProcessClaimOnceStore(repeatedTp), db),
            repeatedServices.GetRequiredService<AuthSecurityEventLogger>());

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var issued = await stepUp.IssueAsync(accountId, userId, TestHarness.Password);
            Assert.True(issued.IsSuccess);

            var validated = await stepUp.ValidateAsync(accountId, userId, issued.Value.Token);
            Assert.True(validated.IsSuccess, $"grant #{attempt} was refused");
        }
    }

    // The over-revocation guard. Recording a logout must bound the PAST, never
    // poison the user: a grant minted AFTER the logout is a fresh, deliberate
    // re-authentication and has to work, or a single logout would permanently
    // lock the user out of privileged administration.
    [Fact]
    public async Task CreateOwner_StepUpIssuedAfterAnAuthenticatedLogout_IsStillAccepted()
    {
        var (owner, account, tokens) = await OwnerSessionAsync();

        await factory.CreateClient(Cookieless)
            .PostLogoutAsync(tokens.RefreshToken, accessToken: tokens.AccessToken, accountId: account);

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
        var (_, account, tokens) = await OwnerSessionAsync();

        var response = await factory.CreateClient(Cookieless)
            .PostLogoutAsync(tokens.RefreshToken, accessToken: tokens.AccessToken, accountId: account);

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
    private async Task<(HttpClient Owner, Guid Account, string RefreshToken, ManualTimeProvider Clock,
        WebApplicationFactory<Program> Host)> FrozenClockOwnerAsync(DateTimeOffset start)
    {
        var email = $"admin-{Guid.NewGuid():N}@test.local";
        var account = await factory.SeedAccountWithUserAsync(email);
        var pair = await factory.LoginAsync(email);

        var clock = new ManualTimeProvider(start);
        var frozen = factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(clock)));
        var owner = frozen.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pair.AccessToken);
        return (owner, account, pair.RefreshToken, clock, frozen);
    }

    private static async Task LogoutAsync(WebApplicationFactory<Program> host, Guid account, string refreshToken)
    {
        // #532 — the pre-per-farm logout read the SHARED cookie, so the cookie's
        // owner was always the bearer here. With per-farm cookies the endpoint
        // reads the NAMED farm's cookie: the presented value rides under this
        // session's own per-farm name and the tab declares it, exactly as the
        // SPA's logout does. The token is the RAW (decoded) value — the server
        // hashes whatever the browser sends, and the browser resends the
        // Set-Cookie value verbatim.
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        request.Headers.Add(AuthCookies.CsrfHeaderName, "1");
        request.Headers.Add("Cookie", AuthCookies.RefreshCookieNameFor(account) + "=" + refreshToken);
        request.Headers.Add(AuthCookies.ExpectedAccountHeaderName, account.ToString());
        var response = await host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false })
            .SendAsync(request);
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
        var (owner, account, refreshToken, clock, host) = await FrozenClockOwnerAsync(anchor.AddMilliseconds(500));

        await LogoutAsync(host, account, refreshToken);

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
        var (owner, account, refreshToken, clock, host) = await FrozenClockOwnerAsync(anchor.AddMilliseconds(200));

        var grant = await StepUpAsync(owner, TestHarness.Password);

        clock.Now = anchor.AddMilliseconds(500);
        await LogoutAsync(host, account, refreshToken);

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
        var (owner, account, refreshToken, _, host) = await FrozenClockOwnerAsync(WholeSecondAnchor().AddMilliseconds(500));

        var grant = await StepUpAsync(owner, TestHarness.Password);
        // Clock untouched: the logout is recorded at the grant's own instant.
        await LogoutAsync(host, account, refreshToken);

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
    public async Task ResetWorkersPassword_MissingStepUp_Is403_AndPasswordUnchanged()
    {
        var (owner, accountId) = await AdminAsync();
        var workerEmail = $"hand-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, workerEmail, asAdmin: false);
        var workerId = (await FindUserAsync(owner, workerEmail)).Id;
        var rejectedPassword = FreshPassword();

        var response = await SetPasswordWithStepUpAsync(
            owner, workerId, rejectedPassword, stepUpToken: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await factory.TryLoginAsync(workerEmail, TestHarness.Password)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await factory.TryLoginAsync(workerEmail, rejectedPassword)).StatusCode);
    }

    [Fact]
    public async Task ResetWorkersPassword_WithValidStepUp_Succeeds()
    {
        var (owner, accountId) = await AdminAsync();
        var workerEmail = $"hand-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, workerEmail, asAdmin: false);
        var workerId = (await FindUserAsync(owner, workerEmail)).Id;
        var grant = await StepUpAsync(owner, TestHarness.Password);
        var newPassword = FreshPassword();

        var response = await SetPasswordWithStepUpAsync(owner, workerId, newPassword, grant.Token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await factory.TryLoginAsync(workerEmail, newPassword)).StatusCode);
    }

    [Fact]
    public async Task ResetPassword_MissingStepUp_IsNonEnumerating_ForKnownAndUnknownTargets()
    {
        var (owner, accountId) = await AdminAsync();
        var workerEmail = $"hand-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, workerEmail, asAdmin: false);
        var workerId = (await FindUserAsync(owner, workerEmail)).Id;
        var rejectedPassword = FreshPassword();

        var known = await SetPasswordWithStepUpAsync(
            owner, workerId, rejectedPassword, stepUpToken: null);
        var unknown = await SetPasswordWithStepUpAsync(
            owner, Guid.NewGuid(), rejectedPassword, stepUpToken: null);
        var knownBody = await known.Content.ReadAsStringAsync();
        var unknownBody = await unknown.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, known.StatusCode);
        Assert.Equal(known.StatusCode, unknown.StatusCode);
        Assert.Equal(knownBody, unknownBody);
        Assert.Equal(HttpStatusCode.OK,
            (await factory.TryLoginAsync(workerEmail, TestHarness.Password)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await factory.TryLoginAsync(workerEmail, rejectedPassword)).StatusCode);
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
