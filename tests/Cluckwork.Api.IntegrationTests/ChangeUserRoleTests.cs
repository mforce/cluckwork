namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Cluckwork.Api.Endpoints.Auth;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #355 — promote/demote an existing user's role. HTTP-driven scenarios that
// exercise the full pipeline (self-target guard, validation, step-up header,
// idempotency). Guard-boundary and race scenarios live in
// ChangeUserRoleRaceTests.cs, resolved directly via DI per that file's own
// header comment — the OwnerOnly route + this file's self-target guard make
// several guard branches unreachable through a legitimate, non-racing,
// distinct HTTP actor (see that file).
[Collection(IntegrationCollection.Name)]
public sealed class ChangeUserRoleTests(CluckworkWebApplicationFactory factory)
{
    private sealed record UserRow(Guid Id, string Email, string? DisplayName, string Role);
    private sealed record StepUpDto(string Token, DateTimeOffset ExpiresAt);

    private static string FreshPassword() => $"Aa1!{Guid.NewGuid():N}";

    private async Task<(HttpClient Owner, Guid AccountId, string Email)> OwnerAsync()
    {
        var email = $"owner-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var owner = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        return (owner, accountId, email);
    }

    private static async Task<UserRow> FindUserAsync(HttpClient owner, string email)
    {
        var users = await owner.GetFromJsonAsync<List<UserRow>>("/api/v1/users");
        return users!.Single(u => u.Email == email);
    }

    private async Task<(string Email, Guid Id)> SeedUserAsync(Guid accountId, string? role)
    {
        var email = $"target-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, email, role);
        using var scope = factory.Services.CreateScope();
        var tenant = scope.ServiceProvider.GetRequiredService<Cluckwork.Infrastructure.Persistence.TenantContext>();
        tenant.Resolve(accountId);
        var identity = scope.ServiceProvider.GetRequiredService<Cluckwork.Application.Common.IIdentityProvider>();
        var user = (await identity.ListUsersAsync(accountId)).Single(u => u.Email == email);
        return (email, user.Id);
    }

    private static async Task<string> StepUpAsync(HttpClient client, string password)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/step-up", new { password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<StepUpDto>())!.Token;
    }

    private static Task<HttpResponseMessage> ChangeRoleAsync(
        HttpClient client, Guid userId, string role, string? stepUpToken = null, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/users/{userId}/role")
        {
            Content = JsonContent.Create(new { role })
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString());
        if (stepUpToken is not null)
            request.Headers.Add(AuthEndpoints.StepUpHeaderName, stepUpToken);
        return client.SendAsync(request);
    }

    // ---------- Ordinary promote/demote ----------

    [Fact]
    public async Task Demote_AnOrdinaryManager_ToReadOnly_Succeeds()
    {
        var (owner, accountId, _) = await OwnerAsync();
        var (email, id) = await SeedUserAsync(accountId, "Manager");

        var response = await ChangeRoleAsync(owner, id, "ReadOnly");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("ReadOnly", (await FindUserAsync(owner, email)).Role);
    }

    [Fact]
    public async Task Promote_AWorker_ToManager_Succeeds()
    {
        var (owner, accountId, _) = await OwnerAsync();
        var (email, id) = await SeedUserAsync(accountId, role: null);

        var response = await ChangeRoleAsync(owner, id, "Manager");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("Manager", (await FindUserAsync(owner, email)).Role);
    }

    [Fact]
    public async Task Demote_AManager_ToWorker_UsesTheWorkerSentinel()
    {
        var (owner, accountId, _) = await OwnerAsync();
        var (email, id) = await SeedUserAsync(accountId, "Manager");

        var response = await ChangeRoleAsync(owner, id, "Worker");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("Worker", (await FindUserAsync(owner, email)).Role);
    }

    // ---------- Self-target guard (#355, mirrors SetUserPassword's precedent) ----------

    [Fact]
    public async Task Owner_ChangingTheirOwnRole_Is400_AndLeavesItUnchanged()
    {
        var (owner, accountId, email) = await OwnerAsync();
        var self = await FindUserAsync(owner, email);

        var response = await ChangeRoleAsync(owner, self.Id, "Manager");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("own role", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Admin", (await FindUserAsync(owner, email)).Role);
    }

    // ---------- Step-up gating (#308) — only when the REQUESTED role is Owner ----------

    [Fact]
    public async Task Promote_ToOwner_WithoutStepUp_Is403_AndLeavesTheRoleUnchanged()
    {
        var (owner, accountId, _) = await OwnerAsync();
        var (email, id) = await SeedUserAsync(accountId, "Manager");

        var response = await ChangeRoleAsync(owner, id, "Admin");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Manager", (await FindUserAsync(owner, email)).Role);
    }

    [Fact]
    public async Task Promote_ToOwner_WithStepUp_Succeeds()
    {
        var (owner, accountId, ownerEmail) = await OwnerAsync();
        var (email, id) = await SeedUserAsync(accountId, "Manager");
        var token = await StepUpAsync(owner, TestHarness.Password);

        var response = await ChangeRoleAsync(owner, id, "Admin", token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("Admin", (await FindUserAsync(owner, email)).Role);
    }

    [Fact]
    public async Task Demote_AnOwner_ToManager_NeedsNoStepUp()
    {
        var (owner, accountId, _) = await OwnerAsync();
        var (email, id) = await SeedUserAsync(accountId, "Admin");

        var response = await ChangeRoleAsync(owner, id, "Manager");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("Manager", (await FindUserAsync(owner, email)).Role);
    }

    // ---------- Non-Owner caller, tenant scoping, validation ----------

    [Fact]
    public async Task NonOwnerCaller_Is403()
    {
        var (owner, accountId, _) = await OwnerAsync();
        var managerEmail = $"mgr-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, managerEmail, role: "Manager");
        var manager = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(managerEmail));
        var (targetEmail, id) = await SeedUserAsync(accountId, role: null);

        var response = await ChangeRoleAsync(manager, id, "Manager");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Worker", (await FindUserAsync(owner, targetEmail)).Role);
    }

    [Fact]
    public async Task UnknownUser_Is404()
    {
        var (owner, _, _) = await OwnerAsync();

        var response = await ChangeRoleAsync(owner, Guid.NewGuid(), "Manager");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UserInAnotherAccount_Is404_AndLeavesTheirRoleUnchanged()
    {
        var foreignEmail = $"foreign-{Guid.NewGuid():N}@test.local";
        var foreignAccount = await factory.SeedAccountWithUserAsync(foreignEmail);
        var foreignOwner = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(foreignEmail));
        var (targetEmail, id) = await SeedUserAsync(foreignAccount, "Manager");

        var (owner, _, _) = await OwnerAsync();
        var response = await ChangeRoleAsync(owner, id, "ReadOnly");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Manager", (await FindUserAsync(foreignOwner, targetEmail)).Role);
    }

    [Theory]
    [InlineData("")]
    [InlineData("NotARealRole")]
    [InlineData("admin")] // case-sensitive — the stored constant is "Admin"
    public async Task UnrecognizedRole_Is400(string role)
    {
        var (owner, accountId, _) = await OwnerAsync();
        var (_, id) = await SeedUserAsync(accountId, "Manager");

        var response = await ChangeRoleAsync(owner, id, role);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------- No-op: skips epoch bump, revoke, and audit entirely ----------

    [Fact]
    public async Task NoOp_SameRoleResubmitted_LeavesTheAccessTokenAndRefreshTokensAlone()
    {
        var (owner, accountId, _) = await OwnerAsync();
        var (email, id) = await SeedUserAsync(accountId, "Manager");
        var target = await factory.LoginAsync(email); // a live session the no-op must not touch

        var response = await ChangeRoleAsync(owner, id, "Manager");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await factory.CreateClient().PostRefreshAsync(target.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task NoOp_WorkerResubmittingWorker_Succeeds()
    {
        // #355 round-2 finding #1 — a Worker target has ZERO role rows, so the
        // no-op check must recognize {} == {} rather than requiring "exactly
        // one row" (which no Worker can ever satisfy).
        var (owner, accountId, _) = await OwnerAsync();
        var (email, id) = await SeedUserAsync(accountId, role: null);

        var response = await ChangeRoleAsync(owner, id, "Worker");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("Worker", (await FindUserAsync(owner, email)).Role);
    }

    [Fact]
    public async Task NoOp_WritesNoAuditRow()
    {
        var (owner, accountId, _) = await OwnerAsync();
        var (_, id) = await SeedUserAsync(accountId, "Manager");
        var before = await factory.WithTenantScopeAsync(accountId,
            async db => await db.AuditEvents.CountAsync(e => e.EntityId == id));

        var response = await ChangeRoleAsync(owner, id, "Manager");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(before, await factory.WithTenantScopeAsync(accountId,
            async db => await db.AuditEvents.CountAsync(e => e.EntityId == id)));
    }

    // ---------- A real change bumps the epoch by exactly 1 and revokes ----------

    [Fact]
    public async Task RealChange_BumpsCredentialEpochByExactlyOne_AndTheOldAccessTokenStopsWorking()
    {
        var (owner, accountId, _) = await OwnerAsync();
        var (email, id) = await SeedUserAsync(accountId, "Manager");
        var target = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        var epochBefore = await factory.WithTenantScopeAsync(accountId, async db =>
            await db.Users.Where(u => u.Id == id).Select(u => u.CredentialEpoch).SingleAsync());

        var response = await ChangeRoleAsync(owner, id, "ReadOnly");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(epochBefore + 1, await factory.WithTenantScopeAsync(accountId, async db =>
            await db.Users.Where(u => u.Id == id).Select(u => u.CredentialEpoch).SingleAsync()));
        // The target's already-issued access token is dead on its very next request.
        Assert.Equal(HttpStatusCode.Unauthorized, (await target.GetAsync("/api/v1/users")).StatusCode);
    }

    [Fact]
    public async Task RealChange_LiterallyMarksTheTargetsRefreshTokensRevoked()
    {
        var (owner, accountId, _) = await OwnerAsync();
        var (email, id) = await SeedUserAsync(accountId, "Manager");
        var target = await factory.LoginAsync(email);

        var response = await ChangeRoleAsync(owner, id, "ReadOnly");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        // Direct assertion, not the indirect "does the access token 401" proof
        // — that alone can pass on an epoch mismatch even if the bulk revoke
        // never ran (#355 round-1 finding #7).
        var revoked = await factory.WithTenantScopeAsync(accountId, async db =>
            await db.RefreshTokens.Where(t => t.UserId == id).Select(t => t.RevokedAt).ToListAsync());
        Assert.NotEmpty(revoked);
        Assert.All(revoked, r => Assert.NotNull(r));
    }

    // ---------- Multi-role adversarial cleanup (#355 round-1 finding #1) ----------

    [Fact]
    public async Task MultiRoleTarget_DemotedToReadOnly_LosesBothStrayRoleRows()
    {
        var (owner, accountId, _) = await OwnerAsync();
        var (email, id) = await SeedUserAsync(accountId, "Admin");
        // Directly pile a second role onto the target, bypassing the normal
        // single-assignment path — Identity permits it even though every
        // ordinary write path here assigns exactly one (AuthPolicies'
        // EffectiveRole/IdentityProvider's Rank both defensively assume this
        // is reachable).
        await factory.AddRoleAsync(email, "Manager");

        var response = await ChangeRoleAsync(owner, id, "ReadOnly");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("ReadOnly", (await FindUserAsync(owner, email)).Role);
        var remainingRoles = await factory.WithTenantScopeAsync(accountId, async db =>
            await (from ur in db.UserRoles
                   join r in db.Roles on ur.RoleId equals r.Id
                   where ur.UserId == id
                   select r.Name).ToListAsync());
        Assert.Equal(["ReadOnly"], remainingRoles);
    }

    [Fact]
    public async Task MultiRoleCleanup_WhenEffectiveRoleUnchanged_IsNotANoOp_AndStillBumpsEpoch()
    {
        // The target's HIGHEST role (Owner) doesn't change, but a stray
        // Manager row is removed underneath it — the set-equality no-op check
        // must NOT treat this as unchanged (#355 round-2 finding #1's fix).
        var (owner, accountId, _) = await OwnerAsync();
        var (email, id) = await SeedUserAsync(accountId, "Admin");
        await factory.AddRoleAsync(email, "Manager");
        var epochBefore = await factory.WithTenantScopeAsync(accountId, async db =>
            await db.Users.Where(u => u.Id == id).Select(u => u.CredentialEpoch).SingleAsync());
        // Requesting "Admin" is still a promotion-to-Owner as far as the
        // step-up gate is concerned (a pure function of the REQUESTED role,
        // decided before IdentityProvider ever sees that this is really a
        // cleanup) — the accepted friction from #355's design grill.
        var token = await StepUpAsync(owner, TestHarness.Password);

        var response = await ChangeRoleAsync(owner, id, "Admin", token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(epochBefore + 1, await factory.WithTenantScopeAsync(accountId, async db =>
            await db.Users.Where(u => u.Id == id).Select(u => u.CredentialEpoch).SingleAsync()));
        var remainingRoles = await factory.WithTenantScopeAsync(accountId, async db =>
            await (from ur in db.UserRoles
                   join r in db.Roles on ur.RoleId equals r.Id
                   where ur.UserId == id
                   select r.Name).ToListAsync());
        Assert.Equal(["Admin"], remainingRoles);
    }

    // ---------- Audit content ----------

    [Fact]
    public async Task RealChange_AuditsOldAndNewRoleArrays()
    {
        var (owner, accountId, ownerEmail) = await OwnerAsync();
        var (_, id) = await SeedUserAsync(accountId, "Manager");

        var response = await ChangeRoleAsync(owner, id, "ReadOnly");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var audit = await factory.WithTenantScopeAsync(accountId, async db =>
            await db.AuditEvents
                .Where(e => e.EntityId == id && e.Action == "User.RoleChanged")
                .OrderByDescending(e => e.OccurredAtUtc)
                .FirstAsync());
        Assert.Contains("Manager", audit.DetailsJson);
        Assert.Contains("ReadOnly", audit.DetailsJson);
    }

    // ---------- Idempotency ----------

    [Fact]
    public async Task IdempotencyReplay_DoesNotConsumeASecondStepUpGrant()
    {
        var (owner, accountId, _) = await OwnerAsync();
        var (email, id) = await SeedUserAsync(accountId, "Manager");
        var token = await StepUpAsync(owner, TestHarness.Password);
        var key = Guid.NewGuid().ToString();

        var first = await ChangeRoleAsync(owner, id, "Admin", token, key);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        // Same key: served from the idempotency cache, never re-executed — so
        // even though the grant is already spent, replaying must still 204,
        // not 403 for "no grant left."
        var replay = await ChangeRoleAsync(owner, id, "Admin", token, key);
        Assert.Equal(HttpStatusCode.NoContent, replay.StatusCode);
    }

    // ---------- SecurityStamp rotation (codex review, PR #475 round-2) ----------

    [Fact]
    public async Task RoleChange_InvalidatesTheTargets_OutstandingStepUpGrant()
    {
        // CredentialEpoch kills the target's bearer/refresh tokens on a role
        // change, but a step-up grant (#308) is validated against
        // SecurityStamp — a separate credential entirely. Without an explicit
        // rotation, a grant the target already holds (issued for some other
        // purpose, moments before their own role changed) would still be
        // spendable after they sign back in with a fresh epoch.
        var (owner, accountId, _) = await OwnerAsync();
        var (targetEmail, targetId) = await SeedUserAsync(accountId, "Manager");
        var targetClient = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(targetEmail));
        var targetGrant = await StepUpAsync(targetClient, TestHarness.Password);

        var response = await ChangeRoleAsync(owner, targetId, "Sales");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<Cluckwork.Infrastructure.Persistence.TenantContext>()
            .Resolve(accountId);
        var stepUpService = scope.ServiceProvider.GetRequiredService<Cluckwork.Application.Common.IStepUpGrantService>();
        var validated = await stepUpService.ValidateAsync(accountId, targetId, targetGrant, CancellationToken.None);

        Assert.True(validated.IsFailure, "a step-up grant issued before the role change must not survive it");
    }

    // ---------- Request body cap ----------

    [Fact]
    public async Task OversizedBody_Is413()
    {
        var (owner, accountId, _) = await OwnerAsync();
        var (_, id) = await SeedUserAsync(accountId, "Manager");

        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/users/{id}/role")
        {
            Content = JsonContent.Create(new { role = new string('a', 4096) })
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await owner.SendAsync(request)).StatusCode);
    }
}
