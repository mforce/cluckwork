namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Net.Http.Headers;
using Cluckwork.Api.Endpoints.Auth;
using Cluckwork.Api.IntegrationTests.Infrastructure;
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

    [Fact]
    public async Task StepUp_WrongCurrentPassword_Is401_AndSaysSo()
    {
        var (owner, _) = await AdminAsync();
        var response = await owner.PostAsJsonAsync("/api/v1/auth/step-up", new { password = FreshPassword() });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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
}
