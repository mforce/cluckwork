namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Cluckwork.Api.Endpoints.Auth;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #356 — disable / re-enable a user. HTTP-driven scenarios exercising the full
// pipeline (self-target guard, step-up header, idempotency, body cap).
//
// The guarantee that makes this feature worth anything is NOT the DisabledAt
// flag — CredentialEpochMiddleware already rejects a disabled user (#364).
// It is the ASYMMETRY: disable bumps CredentialEpoch, enable does not and does
// not restore the old value, so re-enabling cannot resurrect a pre-disable
// access token. A boolean-only implementation passes every "disabled user is
// blocked" test in this file and fails exactly one:
// Enable_DoesNotBumpTheEpoch_AndThePreDisableAccessTokenStaysDead.
//
// Guard-boundary and race scenarios live in DisableUserRaceTests.cs — the
// OwnerOnly route plus this file's self-target guard make Users.LastOwner
// unreachable through a legitimate, non-racing, distinct HTTP actor, exactly
// as ChangeUserRoleRaceTests documents for #355.
[Collection(IntegrationCollection.Name)]
public sealed class DisableUserTests(CluckworkWebApplicationFactory factory)
{
    private sealed record UserRow(Guid Id, string Email, string? DisplayName, string Role, DateTimeOffset? DisabledAt);
    private sealed record StepUpDto(string Token, DateTimeOffset ExpiresAt);

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
        scope.ServiceProvider
            .GetRequiredService<Cluckwork.Infrastructure.Persistence.TenantContext>()
            .Resolve(accountId);
        var identity = scope.ServiceProvider.GetRequiredService<Cluckwork.Application.Common.IIdentityProvider>();
        var user = (await identity.ListUsersAsync(accountId)).Single(u => u.Email == email);
        return (email, user.Id);
    }

    private static async Task<string> StepUpAsync(HttpClient client) =>
        (await (await client.PostAsJsonAsync("/api/v1/auth/step-up", new { password = TestHarness.Password }))
            .EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<StepUpDto>())!.Token;

    private static Task<HttpResponseMessage> DisableAsync(
        HttpClient client, Guid userId, string? stepUpToken, string? reason = null, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/users/{userId}/disable")
        {
            Content = JsonContent.Create(new { reason }),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString());
        if (stepUpToken is not null)
            request.Headers.Add(AuthEndpoints.StepUpHeaderName, stepUpToken);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> EnableAsync(
        HttpClient client, Guid userId, string? stepUpToken, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/users/{userId}/enable");
        request.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString());
        if (stepUpToken is not null)
            request.Headers.Add(AuthEndpoints.StepUpHeaderName, stepUpToken);
        return client.SendAsync(request);
    }

    private Task<int> EpochAsync(Guid accountId, Guid userId) =>
        factory.WithTenantScopeAsync(accountId, async db =>
            await db.Users.Where(u => u.Id == userId).Select(u => u.CredentialEpoch).SingleAsync());

    // ---------- Happy path ----------

    [Fact]
    public async Task Disable_AnOrdinaryManager_Is204_AndTheListShowsDisabledAt()
    {
        var (owner, accountId, _) = await OwnerAsync();
        var (email, id) = await SeedUserAsync(accountId, "Manager");

        var response = await DisableAsync(owner, id, await StepUpAsync(owner));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.NotNull((await FindUserAsync(owner, email)).DisabledAt);
    }

    [Fact]
    public async Task DisabledUser_StillAppearsInTheList()
    {
        // The SPA lists disabled users inline (muted row + badge) rather than
        // filtering them out — an Owner cannot re-enable someone they cannot see.
        var (owner, accountId, _) = await OwnerAsync();
        var (email, id) = await SeedUserAsync(accountId, "Manager");

        Assert.Equal(HttpStatusCode.NoContent,
            (await DisableAsync(owner, id, await StepUpAsync(owner))).StatusCode);

        var users = await owner.GetFromJsonAsync<List<UserRow>>("/api/v1/users");
        Assert.Contains(users!, u => u.Email == email);
    }

    [Fact]
    public async Task Enable_RestoresLogin_AndClearsDisabledAt()
    {
        var (owner, accountId, _) = await OwnerAsync();
        var (email, id) = await SeedUserAsync(accountId, "Manager");
        Assert.Equal(HttpStatusCode.NoContent,
            (await DisableAsync(owner, id, await StepUpAsync(owner))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.TryLoginAsync(email, TestHarness.Password)).StatusCode);

        var response = await EnableAsync(owner, id, await StepUpAsync(owner));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null((await FindUserAsync(owner, email)).DisabledAt);
        Assert.Equal(HttpStatusCode.OK, (await factory.TryLoginAsync(email, TestHarness.Password)).StatusCode);
    }

    [Fact]
    public async Task Enable_ClearsDisabledBy_NotJustDisabledAt()
    {
        // The pair describes ONE live fact. A stale DisabledBy left on an active
        // user is a column that reads as current and is not; the history lives
        // in the audit log, which both directions write.
        var (owner, accountId, _) = await OwnerAsync();
        var (_, id) = await SeedUserAsync(accountId, "Manager");
        await DisableAsync(owner, id, await StepUpAsync(owner));
        await EnableAsync(owner, id, await StepUpAsync(owner));

        var row = await factory.WithTenantScopeAsync(accountId, async db => await db.Users
            .Where(u => u.Id == id)
            .Select(u => new { u.DisabledAt, u.DisabledBy })
            .SingleAsync());

        Assert.Null(row.DisabledAt);
        Assert.Null(row.DisabledBy);
    }

    // ---------- The asymmetry: this is the whole feature ----------

    [Fact]
    public async Task Disable_BumpsTheEpochByExactlyOne_AndTheLiveAccessTokenDiesOnTheNextRequest()
    {
        var (owner, accountId, _) = await OwnerAsync();
        var (email, id) = await SeedUserAsync(accountId, "Manager");
        var target = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        var epochBefore = await EpochAsync(accountId, id);

        Assert.Equal(HttpStatusCode.NoContent,
            (await DisableAsync(owner, id, await StepUpAsync(owner))).StatusCode);

        // EXACTLY one, asserted directly. The 401 below is NOT evidence of a
        // bump: CredentialEpochMiddleware rejects on DisabledAt too, so it
        // answers 401 whatever the epoch does. Without this line the test's own
        // name is a claim it does not make, and `CredentialEpoch = 0` — the
        // value #364 permanently retired, which would re-arm every legacy
        // IssuedEpoch-0 refresh row and every token with a missing claim —
        // sails through the whole suite.
        var epochAfter = await EpochAsync(accountId, id);
        Assert.Equal(epochBefore + 1, epochAfter);
        Assert.True(epochAfter > 0, "epoch 0 is permanently retired (#364)");
        Assert.Equal(HttpStatusCode.Unauthorized, (await target.GetAsync("/api/v1/users")).StatusCode);
    }

    [Fact]
    public async Task Enable_DoesNotBumpTheEpoch_AndThePreDisableAccessTokenStaysDead()
    {
        // THE test. Disable kills the epoch-N token by moving the DB to N+1.
        // If enable bumped the epoch again, or restored N, or if the whole
        // mechanism degraded to a DisabledAt boolean, that token comes back to
        // life the moment the user is re-enabled. Every other test in this file
        // still passes under all three of those bugs.
        var (owner, accountId, _) = await OwnerAsync();
        var (email, id) = await SeedUserAsync(accountId, "Manager");
        var target = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        await DisableAsync(owner, id, await StepUpAsync(owner));
        var epochWhileDisabled = await EpochAsync(accountId, id);
        Assert.Equal(HttpStatusCode.NoContent,
            (await EnableAsync(owner, id, await StepUpAsync(owner))).StatusCode);

        Assert.Equal(epochWhileDisabled, await EpochAsync(accountId, id));
        var afterEnable = await target.GetAsync("/api/v1/users");
        Assert.Equal(HttpStatusCode.Unauthorized, afterEnable.StatusCode);
        Assert.Equal("Auth.CredentialsSuperseded",
            (await afterEnable.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
    }

    [Fact]
    public async Task Enable_DoesNotResurrectAPreDisableRefreshToken()
    {
        var (owner, accountId, _) = await OwnerAsync();
        var (email, id) = await SeedUserAsync(accountId, "Manager");
        var target = await factory.LoginAsync(email);

        await DisableAsync(owner, id, await StepUpAsync(owner));
        await EnableAsync(owner, id, await StepUpAsync(owner));

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await factory.CreateClient().PostRefreshAsync(target.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task Disable_LiterallyMarksTheTargetsRefreshTokensRevoked()
    {
        // Direct assertion, not the indirect "the access token 401s" proof —
        // that alone passes on an epoch mismatch even if the bulk revoke never
        // ran (#355 round-1 finding #7, same trap here).
        var (owner, accountId, _) = await OwnerAsync();
        var (email, id) = await SeedUserAsync(accountId, "Manager");
        await factory.LoginAsync(email);

        Assert.Equal(HttpStatusCode.NoContent,
            (await DisableAsync(owner, id, await StepUpAsync(owner))).StatusCode);

        var revoked = await factory.WithTenantScopeAsync(accountId, async db =>
            await db.RefreshTokens.Where(t => t.UserId == id).Select(t => t.RevokedAt).ToListAsync());
        Assert.NotEmpty(revoked);
        Assert.All(revoked, r => Assert.NotNull(r));
    }

    private Task<(string Security, string Concurrency)> StampsAsync(Guid accountId, Guid userId) =>
        factory.WithTenantScopeAsync(accountId, async db => await db.Users
            .Where(u => u.Id == userId)
            .Select(u => new ValueTuple<string, string>(u.SecurityStamp!, u.ConcurrencyStamp!))
            .SingleAsync());

    [Fact]
    public async Task Disable_RotatesBothStamps()
    {
        // Asserted on the STAMP, not on a grant going stale, because the
        // behavioural version is masked: every grant test below disables and
        // then re-enables, and the enable rotates the stamp too — so removing
        // the rotation from the DISABLE path leaves those tests green. Found by
        // mutation, after the enable-side rotation was added; the behavioural
        // assertions stay, but this is the one that actually pins the disable.
        var (owner, accountId, _) = await OwnerAsync();
        var (_, id) = await SeedUserAsync(accountId, "Manager");
        var before = await StampsAsync(accountId, id);

        Assert.Equal(HttpStatusCode.NoContent,
            (await DisableAsync(owner, id, await StepUpAsync(owner))).StatusCode);

        var after = await StampsAsync(accountId, id);
        Assert.NotEqual(before.Security, after.Security);
        Assert.NotEqual(before.Concurrency, after.Concurrency);
    }

    [Fact]
    public async Task Enable_RotatesBothStamps_SoAStaleFullEntityWriteCannotRevertIt()
    {
        // The lost update this closes: a plain SaveChangesAsync leaves
        // ConcurrencyStamp untouched, and Identity's UserStore.UpdateAsync
        // issues a FULL-ENTITY update guarded on that stamp. A concurrent
        // SetUserPassword that read the user BEFORE the enable — and then spent
        // the whole PBKDF2 window before writing — would still match the
        // unrotated stamp and write DisabledAt back from its stale snapshot.
        // The Owner would see 204 and a User.Enabled audit row for an enable
        // that never survived.
        //
        // Note this is NOT what ConcurrentStampChange_DuringTheEnableItself_Is409
        // pins: there the FENCE changes the stamp, which EF's own concurrency
        // token catches with or without the rotation. Only the rotation itself
        // makes the stale writer lose, so only a direct assertion on the stamp
        // value can pin it.
        var (owner, accountId, _) = await OwnerAsync();
        var (_, id) = await SeedUserAsync(accountId, "Manager");
        await DisableAsync(owner, id, await StepUpAsync(owner));
        var before = await StampsAsync(accountId, id);

        Assert.Equal(HttpStatusCode.NoContent,
            (await EnableAsync(owner, id, await StepUpAsync(owner))).StatusCode);

        var after = await StampsAsync(accountId, id);
        Assert.NotEqual(before.Security, after.Security);
        Assert.NotEqual(before.Concurrency, after.Concurrency);
    }

    [Fact]
    public async Task Disable_InvalidatesTheTargetsOutstandingStepUpGrant()
    {
        // A step-up grant (#308) is a THIRD credential, validated against
        // SecurityStamp rather than the epoch — so disable must rotate the
        // stamp, exactly as ChangeUserRoleAsync does.
        var (owner, accountId, _) = await OwnerAsync();
        var (targetEmail, targetId) = await SeedUserAsync(accountId, "Manager");
        var targetClient = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(targetEmail));
        var targetGrant = await StepUpAsync(targetClient);

        Assert.Equal(HttpStatusCode.NoContent,
            (await DisableAsync(owner, targetId, await StepUpAsync(owner))).StatusCode);
        // Re-enable, so the grant's failure cannot be explained by DisabledAt
        // alone — it must be the rotated stamp.
        Assert.Equal(HttpStatusCode.NoContent,
            (await EnableAsync(owner, targetId, await StepUpAsync(owner))).StatusCode);

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider
            .GetRequiredService<Cluckwork.Infrastructure.Persistence.TenantContext>()
            .Resolve(accountId);
        var stepUp = scope.ServiceProvider
            .GetRequiredService<Cluckwork.Application.Common.IStepUpGrantService>();

        Assert.True((await stepUp.ValidateAsync(accountId, targetId, targetGrant, CancellationToken.None)).IsFailure,
            "a step-up grant issued before the disable must not survive a re-enable");
    }

    // ---------- Self-target guard ----------

    [Fact]
    public async Task Owner_DisablingThemselves_Is400_AndStaysActive()
    {
        var (owner, accountId, email) = await OwnerAsync();
        var self = await FindUserAsync(owner, email);

        var response = await DisableAsync(owner, self.Id, await StepUpAsync(owner));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Users.CannotDisableSelf",
            (await response.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
        Assert.Null((await FindUserAsync(owner, email)).DisabledAt);
    }

    [Fact]
    public async Task Owner_EnablingThemselves_Is400()
    {
        // Symmetric with disable. Reaching it requires an active caller, so it
        // is always a no-op in practice — but leaving the hole open means the
        // self-target rule depends on which verb you pick.
        var (owner, _, email) = await OwnerAsync();
        var self = await FindUserAsync(owner, email);

        var response = await EnableAsync(owner, self.Id, await StepUpAsync(owner));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Users.CannotEnableSelf",
            (await response.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
    }

    // ---------- Step-up gating, both directions ----------

    [Fact]
    public async Task Disable_WithoutStepUp_Is403_AndLeavesTheUserActive()
    {
        var (owner, accountId, _) = await OwnerAsync();
        var (email, id) = await SeedUserAsync(accountId, "Manager");

        var response = await DisableAsync(owner, id, stepUpToken: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null((await FindUserAsync(owner, email)).DisabledAt);
    }

    [Fact]
    public async Task Enable_WithoutStepUp_Is403_AndLeavesTheUserDisabled()
    {
        var (owner, accountId, _) = await OwnerAsync();
        var (email, id) = await SeedUserAsync(accountId, "Manager");
        await DisableAsync(owner, id, await StepUpAsync(owner));

        var response = await EnableAsync(owner, id, stepUpToken: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull((await FindUserAsync(owner, email)).DisabledAt);
    }

    // ---------- Tenant scoping and authorization ----------

    [Fact]
    public async Task UnknownUser_Is404()
    {
        var (owner, _, _) = await OwnerAsync();

        Assert.Equal(HttpStatusCode.NotFound,
            (await DisableAsync(owner, Guid.NewGuid(), await StepUpAsync(owner))).StatusCode);
    }

    [Fact]
    public async Task UserInAnotherAccount_Is404_AndStaysActive()
    {
        var foreignEmail = $"foreign-{Guid.NewGuid():N}@test.local";
        var foreignAccount = await factory.SeedAccountWithUserAsync(foreignEmail);
        var foreignOwner = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(foreignEmail));
        var (targetEmail, id) = await SeedUserAsync(foreignAccount, "Manager");

        var (owner, _, _) = await OwnerAsync();
        var response = await DisableAsync(owner, id, await StepUpAsync(owner));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null((await FindUserAsync(foreignOwner, targetEmail)).DisabledAt);
    }

    [Fact]
    public async Task NonOwnerCaller_Is403_AndTheTargetStaysActive()
    {
        var (owner, accountId, _) = await OwnerAsync();
        var managerEmail = $"mgr-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, managerEmail, role: "Manager");
        var manager = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(managerEmail));
        var (targetEmail, id) = await SeedUserAsync(accountId, role: null);

        var response = await DisableAsync(manager, id, stepUpToken: await StepUpAsync(manager));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null((await FindUserAsync(owner, targetEmail)).DisabledAt);
    }

    // ---------- No-ops skip every side effect ----------

    [Fact]
    public async Task Disable_Twice_Is204_AndDoesNotBumpTheEpochASecondTime()
    {
        var (owner, accountId, _) = await OwnerAsync();
        var (_, id) = await SeedUserAsync(accountId, "Manager");
        await DisableAsync(owner, id, await StepUpAsync(owner));
        var epochAfterFirst = await EpochAsync(accountId, id);

        var second = await DisableAsync(owner, id, await StepUpAsync(owner));

        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.Equal(epochAfterFirst, await EpochAsync(accountId, id));
    }

    [Fact]
    public async Task Disable_Twice_DoesNotOverwriteTheOriginalDisabledAt()
    {
        // A second disable must not restamp "when were they disabled" — that
        // timestamp is the answer an Owner reads off the list.
        var (owner, accountId, _) = await OwnerAsync();
        var (email, id) = await SeedUserAsync(accountId, "Manager");
        await DisableAsync(owner, id, await StepUpAsync(owner));
        var first = (await FindUserAsync(owner, email)).DisabledAt;

        await DisableAsync(owner, id, await StepUpAsync(owner));

        Assert.Equal(first, (await FindUserAsync(owner, email)).DisabledAt);
    }

    [Fact]
    public async Task Enable_OnAnAlreadyActiveUser_Is204_AndWritesNoAuditRow()
    {
        var (owner, accountId, _) = await OwnerAsync();
        var (_, id) = await SeedUserAsync(accountId, "Manager");
        var before = await factory.WithTenantScopeAsync(accountId,
            async db => await db.AuditEvents.CountAsync(e => e.EntityId == id));

        var response = await EnableAsync(owner, id, await StepUpAsync(owner));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(before, await factory.WithTenantScopeAsync(accountId,
            async db => await db.AuditEvents.CountAsync(e => e.EntityId == id)));
    }

    // ---------- Audit ----------

    [Fact]
    public async Task Disable_RecordsWhoDidIt_AndAuditsTheReason()
    {
        var (owner, accountId, ownerEmail) = await OwnerAsync();
        var ownerId = (await FindUserAsync(owner, ownerEmail)).Id;
        var (_, id) = await SeedUserAsync(accountId, "Manager");

        Assert.Equal(HttpStatusCode.NoContent,
            (await DisableAsync(owner, id, await StepUpAsync(owner), reason: "Left the farm")).StatusCode);

        Assert.Equal(ownerId, await factory.WithTenantScopeAsync(accountId, async db =>
            await db.Users.Where(u => u.Id == id).Select(u => u.DisabledBy).SingleAsync()));
        var audit = await factory.WithTenantScopeAsync(accountId, async db => await db.AuditEvents
            .Where(e => e.EntityId == id && e.Action == "User.Disabled")
            .OrderByDescending(e => e.OccurredAtUtc)
            .FirstAsync());
        Assert.Equal("Left the farm", audit.Reason);
    }

    [Fact]
    public async Task Enable_WritesItsOwnAuditAction()
    {
        var (owner, accountId, _) = await OwnerAsync();
        var (_, id) = await SeedUserAsync(accountId, "Manager");
        await DisableAsync(owner, id, await StepUpAsync(owner));

        Assert.Equal(HttpStatusCode.NoContent,
            (await EnableAsync(owner, id, await StepUpAsync(owner))).StatusCode);

        Assert.True(await factory.WithTenantScopeAsync(accountId, async db =>
            await db.AuditEvents.AnyAsync(e => e.EntityId == id && e.Action == "User.Enabled")));
    }

    // ---------- Validation and the body cap ----------

    [Fact]
    public async Task Disable_WithAnOverlongReason_Is400_AndLeavesTheUserActive()
    {
        var (owner, accountId, _) = await OwnerAsync();
        var (email, id) = await SeedUserAsync(accountId, "Manager");

        var response = await DisableAsync(owner, id, await StepUpAsync(owner), reason: new string('a', 201));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null((await FindUserAsync(owner, email)).DisabledAt);
    }

    [Fact]
    public async Task Disable_WithNoReason_Is204()
    {
        var (owner, accountId, _) = await OwnerAsync();
        var (email, id) = await SeedUserAsync(accountId, "Manager");

        Assert.Equal(HttpStatusCode.NoContent,
            (await DisableAsync(owner, id, await StepUpAsync(owner), reason: null)).StatusCode);
        Assert.NotNull((await FindUserAsync(owner, email)).DisabledAt);
    }

    [Fact]
    public async Task Disable_WithNoBodyAtAll_Is204()
    {
        // The endpoint declares `DisableUserRequest?`, so an EMPTY body binds
        // null. Every other test sends {"reason":null}, which is not the same
        // request — without this, tightening the parameter to non-nullable is
        // green everywhere and the documented contract quietly stops holding.
        //
        // The Content-Type is required and is not incidental to this test —
        // see Disable_WithNoContentTypeAtAll_Is404_NotUnsupportedMediaType,
        // which asserts that behaviour rather than leaving it as a claim in a
        // comment here.
        var (owner, accountId, _) = await OwnerAsync();
        var (email, id) = await SeedUserAsync(accountId, "Manager");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/users/{id}/disable")
        {
            Content = new StringContent("", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.Add(AuthEndpoints.StepUpHeaderName, await StepUpAsync(owner));

        Assert.Equal(HttpStatusCode.NoContent, (await owner.SendAsync(request)).StatusCode);
        Assert.NotNull((await FindUserAsync(owner, email)).DisabledAt);
    }

    [Fact]
    public async Task Disable_WithNoContentTypeAtAll_Is404_NotUnsupportedMediaType()
    {
        // Surprising enough to pin rather than describe in a comment. The body
        // parameter gives this endpoint application/json Accepts metadata, so a
        // request with no Content-Type is dropped from the candidate set by the
        // consumes matcher — and Program.cs's `/api/{**rest}` catch-all then
        // answers 404, NOT the 415 the shape suggests. A future reader debugging
        // "why is my POST 404-ing" should find this asserted, not inferred.
        var (owner, accountId, _) = await OwnerAsync();
        var (email, id) = await SeedUserAsync(accountId, "Manager");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/users/{id}/disable");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.Add(AuthEndpoints.StepUpHeaderName, await StepUpAsync(owner));

        Assert.Equal(HttpStatusCode.NotFound, (await owner.SendAsync(request)).StatusCode);
        Assert.Null((await FindUserAsync(owner, email)).DisabledAt);
    }

    [Fact]
    public async Task Disable_WithAReasonOfExactlyTheMaximumLength_Is204_AndStoresItWhole()
    {
        // Pins the boundary in the ACCEPTING direction. Only testing 201 leaves
        // MaximumLength(150) — or any other value below 200 — passing, so the
        // documented 200-character cap would not actually be the cap.
        var (owner, accountId, _) = await OwnerAsync();
        var (_, id) = await SeedUserAsync(accountId, "Manager");
        var reason = new string('r', 200);

        Assert.Equal(HttpStatusCode.NoContent,
            (await DisableAsync(owner, id, await StepUpAsync(owner), reason)).StatusCode);

        var audit = await factory.WithTenantScopeAsync(accountId, async db => await db.AuditEvents
            .Where(e => e.EntityId == id && e.Action == "User.Disabled")
            .OrderByDescending(e => e.OccurredAtUtc)
            .FirstAsync());
        Assert.Equal(reason, audit.Reason);
    }

    [Fact]
    public async Task Disable_TrimsTheReason_AndTreatsAWhitespaceOnlyOneAsNone()
    {
        var (owner, accountId, _) = await OwnerAsync();
        var (_, trimmedId) = await SeedUserAsync(accountId, "Manager");
        var (_, blankId) = await SeedUserAsync(accountId, "Manager");

        await DisableAsync(owner, trimmedId, await StepUpAsync(owner), "  spaced out  ");
        await DisableAsync(owner, blankId, await StepUpAsync(owner), "   ");

        Task<string?> ReasonFor(Guid id) => factory.WithTenantScopeAsync(accountId, async db => await db.AuditEvents
            .Where(e => e.EntityId == id && e.Action == "User.Disabled")
            .OrderByDescending(e => e.OccurredAtUtc)
            .Select(e => e.Reason)
            .FirstAsync());

        Assert.Equal("spaced out", await ReasonFor(trimmedId));
        Assert.Null(await ReasonFor(blankId));
    }

    [Fact]
    public async Task Disable_OversizedBody_Is413()
    {
        var (owner, accountId, _) = await OwnerAsync();
        var (_, id) = await SeedUserAsync(accountId, "Manager");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/users/{id}/disable")
        {
            Content = JsonContent.Create(new { reason = new string('a', 8192) }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await owner.SendAsync(request)).StatusCode);
    }

    // ---------- Idempotency ----------

    [Fact]
    public async Task IdempotencyReplay_DoesNotConsumeASecondStepUpGrant()
    {
        var (owner, accountId, _) = await OwnerAsync();
        var (_, id) = await SeedUserAsync(accountId, "Manager");
        var token = await StepUpAsync(owner);
        var key = Guid.NewGuid().ToString();

        Assert.Equal(HttpStatusCode.NoContent, (await DisableAsync(owner, id, token, idempotencyKey: key)).StatusCode);
        // Same key: served from the idempotency cache, never re-executed — so
        // even though the grant is already spent, the replay must still 204.
        Assert.Equal(HttpStatusCode.NoContent, (await DisableAsync(owner, id, token, idempotencyKey: key)).StatusCode);
    }
}
