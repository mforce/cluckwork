namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;

// #165 — password management. Two halves with different rules: an Owner sets
// another user's password without the current one, and any signed-in user
// changes their OWN by proving the current one. Both revoke every refresh token
// for that user, so the change actually evicts other sessions.
[Collection(IntegrationCollection.Name)]
public sealed class UserPasswordTests(CluckworkWebApplicationFactory factory)
{
    private sealed record UserRow(Guid Id, string Email, string? DisplayName, string Role);

    // Runtime-generated and policy-compliant (upper/lower/digit/symbol, >= 12) —
    // never a literal secret in source.
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

    // Seeds a worker in the admin's account and returns (email, id).
    private async Task<(string Email, Guid Id)> SeedWorkerAsync(HttpClient admin, Guid accountId)
    {
        var email = $"hand-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, email, asAdmin: false);
        return (email, (await FindUserAsync(admin, email)).Id);
    }

    // ---------- Admin sets another user's password ----------

    [Fact]
    public async Task AdminSet_ReplacesThePassword_OldOneNoLongerWorks()
    {
        var (admin, accountId) = await AdminAsync();
        var (email, id) = await SeedWorkerAsync(admin, accountId);
        var newPassword = FreshPassword();

        var set = await admin.PutWithKeyAsync($"/api/v1/users/{id}/password",
            Guid.NewGuid().ToString(), new { newPassword });
        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);

        // The new password signs in; the seeded one no longer does.
        Assert.Equal(HttpStatusCode.OK, (await factory.TryLoginAsync(email, newPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await factory.TryLoginAsync(email, TestHarness.Password)).StatusCode);
    }

    [Fact]
    public async Task AdminSet_RevokesTheTargetsExistingSessions()
    {
        var (admin, accountId) = await AdminAsync();
        var (email, id) = await SeedWorkerAsync(admin, accountId);

        // The worker is signed in BEFORE the reset — this is the session a reset
        // has to evict (otherwise a compromised session survives the reset).
        var worker = await factory.LoginAsync(email);
        var refreshed = await factory.CreateClient().PostRefreshAsync(worker.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        // Assert against the LIVE token the rotation produced, not the one just
        // consumed: replaying the consumed token would 401 once the #176 reuse
        // grace expires, passing even if the reset revoked nothing (slow-CI
        // false green — #165 review).
        var live = await TestHarness.ReadTokensAsync(refreshed);

        var reset = await admin.PutWithKeyAsync($"/api/v1/users/{id}/password",
            Guid.NewGuid().ToString(), new { newPassword = FreshPassword() });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        // The worker's CURRENT token is dead: no new access tokens for the old holder.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await factory.CreateClient().PostRefreshAsync(live.RefreshToken)).StatusCode);
    }

    // #165 review — the admin path deliberately skips the current-password proof,
    // so it must refuse self-targeting: otherwise a stolen access token (good for
    // ~15 min) could be converted into a permanent credential takeover, bypassing
    // the check /auth/change-password exists to enforce.
    [Fact]
    public async Task AdminSet_OnSelf_IsRefused_AndLeavesTheOwnPasswordIntact()
    {
        var email = $"admin-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        var admin = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        var self = await FindUserAsync(admin, email);

        var response = await admin.PutWithKeyAsync($"/api/v1/users/{self.Id}/password",
            Guid.NewGuid().ToString(), new { newPassword = FreshPassword() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Account screen", await response.Content.ReadAsStringAsync());
        // Their own password is untouched — no self-inflicted takeover or lockout.
        Assert.Equal(HttpStatusCode.OK,
            (await factory.TryLoginAsync(email, TestHarness.Password)).StatusCode);
    }

    // #165 review — the change-password response carries an access token AND the
    // rotated refresh cookie, so it must be exempt from the idempotency response
    // cache: caching would persist the token and replay it without Set-Cookie.
    // It therefore needs no Idempotency-Key at all.
    [Fact]
    public async Task SelfChange_NeedsNoIdempotencyKey_AndIsNotReplayedFromCache()
    {
        var (admin, accountId) = await AdminAsync();
        var (email, _) = await SeedWorkerAsync(admin, accountId);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        var newPassword = FreshPassword();

        // No Idempotency-Key header at all — the exemption must let it through.
        var first = await client.PostAsJsonAsync("/api/v1/auth/change-password",
            new { currentPassword = TestHarness.Password, newPassword });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        // The rotated cookie really is on the response (a cached replay would omit it).
        Assert.Contains(first.Headers.GetValues("Set-Cookie"),
            c => c.StartsWith("cluckwork_rt=", StringComparison.Ordinal));

        // Repeating the identical request is NOT served from cache — it re-executes
        // and now fails, because the current password has changed.
        var repeat = await client.PostAsJsonAsync("/api/v1/auth/change-password",
            new { currentPassword = TestHarness.Password, newPassword });
        Assert.Equal(HttpStatusCode.BadRequest, repeat.StatusCode);
    }

    [Fact]
    public async Task AdminSet_UnknownUser_Is404()
    {
        var (admin, _) = await AdminAsync();
        var response = await admin.PutWithKeyAsync($"/api/v1/users/{Guid.NewGuid()}/password",
            Guid.NewGuid().ToString(), new { newPassword = FreshPassword() });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AdminSet_UserInAnotherAccount_Is404_AndLeavesThatPasswordIntact()
    {
        var foreignEmail = $"foreign-{Guid.NewGuid():N}@test.local";
        var foreignAccount = await factory.SeedAccountWithUserAsync(foreignEmail);
        var foreignAdmin = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(foreignEmail));
        var foreignUser = await FindUserAsync(foreignAdmin, foreignEmail);

        var (admin, _) = await AdminAsync();
        var response = await admin.PutWithKeyAsync($"/api/v1/users/{foreignUser.Id}/password",
            Guid.NewGuid().ToString(), new { newPassword = FreshPassword() });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        // Untouched: the foreign user still signs in with their original password.
        Assert.Equal(HttpStatusCode.OK,
            (await factory.TryLoginAsync(foreignEmail, TestHarness.Password)).StatusCode);
    }

    [Fact]
    public async Task AdminSet_TooShort_Is400()
    {
        var (admin, accountId) = await AdminAsync();
        var (_, id) = await SeedWorkerAsync(admin, accountId);

        var response = await admin.PutWithKeyAsync($"/api/v1/users/{id}/password",
            Guid.NewGuid().ToString(), new { newPassword = "Aa1!short" }); // 9 chars
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AdminSet_AsManager_Is403()
    {
        var (admin, accountId) = await AdminAsync();
        var managerEmail = $"mgr-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, managerEmail, role: "Manager");
        var manager = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(managerEmail));
        var (workerEmail, id) = await SeedWorkerAsync(admin, accountId);

        var response = await manager.PutWithKeyAsync($"/api/v1/users/{id}/password",
            Guid.NewGuid().ToString(), new { newPassword = FreshPassword() });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        // The worker's password is untouched.
        Assert.Equal(HttpStatusCode.OK,
            (await factory.TryLoginAsync(workerEmail, TestHarness.Password)).StatusCode);
    }

    // ---------- Self-service change ----------

    [Fact]
    public async Task SelfChange_WithCorrectCurrent_Works_AndSignsOutOtherDevices()
    {
        var (admin, accountId) = await AdminAsync();
        var (email, _) = await SeedWorkerAsync(admin, accountId);

        // Two devices signed in; the change happens on "this" one.
        var otherDevice = await factory.LoginAsync(email);
        var thisDevice = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        var newPassword = FreshPassword();
        var response = await thisDevice.PostWithKeyAsync("/api/v1/auth/change-password",
            Guid.NewGuid().ToString(),
            new { currentPassword = TestHarness.Password, newPassword });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The caller is handed a fresh pair (stays signed in here).
        var handed = await TestHarness.ReadTokensAsync(response);
        Assert.False(string.IsNullOrWhiteSpace(handed.AccessToken));
        Assert.Equal(HttpStatusCode.OK,
            (await factory.CreateClient().PostRefreshAsync(handed.RefreshToken)).StatusCode);

        // The OTHER device's session is gone.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await factory.CreateClient().PostRefreshAsync(otherDevice.RefreshToken)).StatusCode);

        // And the credential really changed.
        Assert.Equal(HttpStatusCode.OK, (await factory.TryLoginAsync(email, newPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await factory.TryLoginAsync(email, TestHarness.Password)).StatusCode);
    }

    [Fact]
    public async Task SelfChange_WrongCurrentPassword_Is400_AndChangesNothing()
    {
        var (admin, accountId) = await AdminAsync();
        var (email, _) = await SeedWorkerAsync(admin, accountId);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        var response = await client.PostWithKeyAsync("/api/v1/auth/change-password",
            Guid.NewGuid().ToString(),
            new { currentPassword = FreshPassword(), newPassword = FreshPassword() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Current password is incorrect", await response.Content.ReadAsStringAsync());
        // Original password still works.
        Assert.Equal(HttpStatusCode.OK,
            (await factory.TryLoginAsync(email, TestHarness.Password)).StatusCode);
    }

    [Fact]
    public async Task SelfChange_RejectsAShortNewPassword_AndReusingTheCurrentOne()
    {
        var (admin, accountId) = await AdminAsync();
        var (email, _) = await SeedWorkerAsync(admin, accountId);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        var tooShort = await client.PostWithKeyAsync("/api/v1/auth/change-password",
            Guid.NewGuid().ToString(),
            new { currentPassword = TestHarness.Password, newPassword = "Aa1!short" });
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);

        var unchanged = await client.PostWithKeyAsync("/api/v1/auth/change-password",
            Guid.NewGuid().ToString(),
            new { currentPassword = TestHarness.Password, newPassword = TestHarness.Password });
        Assert.Equal(HttpStatusCode.BadRequest, unchanged.StatusCode);
    }

    [Fact]
    public async Task SelfChange_RequiresAuthentication()
    {
        var response = await factory.CreateClient().PostWithKeyAsync("/api/v1/auth/change-password",
            Guid.NewGuid().ToString(),
            new { currentPassword = TestHarness.Password, newPassword = FreshPassword() });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
