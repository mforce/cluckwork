namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Cluckwork.Api.Endpoints.Auth;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Users.DisableUser;
using Cluckwork.Application.Features.Users.EnableUser;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #356 — the guards that a sequential test cannot reach.
//
// The last-active-Owner guard is NOT race-safe on the strength of
// ConcurrencyStamp: Owners A and B disabling each other touch DIFFERENT rows
// and share no concurrency token, so each reads "two active Owners", each
// commits, and the account is left with zero working Owners and no way back in
// short of `recover-admin`. Only an account-wide FOR UPDATE lock closes it.
// MutualDisableRace_TheLockSerializes_AndTheAccountKeepsAnActiveOwner is the
// test that fails without it; every sequential test in DisableUserTests.cs
// passes with the bug present.
//
// Same structure as ChangeUserRoleRaceTests (#355): a raw FOR UPDATE fence held
// on a hand-built AppDbContext — not factory.Services, whose context carries
// EnableRetryOnFailure and cannot be held across several hand-controlled steps
// — with REAL handlers resolved from DI on the other side. The OwnerOnly route
// plus the endpoint's self-target guard make Users.LastOwner unreachable over
// HTTP with a distinct actor, so the boundary cases below resolve the handler
// directly and pass actingUserId == userId on purpose.
[Collection(IntegrationCollection.Name)]
public sealed class DisableUserRaceTests(CluckworkWebApplicationFactory factory)
{
    private sealed record StepUpDto(string Token, DateTimeOffset ExpiresAt);

    // AspNetUsers' UserNameIndex is a GLOBAL unique index, not account-scoped —
    // every seeded email must be unique per TEST, not merely per account.
    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}@test.local";

    private async Task<Guid> SeedOwnerFarmAsync(params string[] emails)
    {
        var accountId = await factory.SeedAccountWithUserAsync(emails[0]);
        foreach (var email in emails.Skip(1))
            await factory.SeedUserAsync(accountId, email, Cluckwork.Domain.Accounts.Roles.Owner);
        return accountId;
    }

    private async Task<Guid> UserIdAsync(Guid accountId, string email)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(accountId);
        var identity = scope.ServiceProvider.GetRequiredService<IIdentityProvider>();
        return (await identity.ListUsersAsync(accountId)).Single(u => u.Email == email).Id;
    }

    private Task ForceDisableAsync(Guid accountId, Guid userId) =>
        factory.WithTenantScopeAsync(accountId, async db =>
        {
            var user = await db.Users.SingleAsync(u => u.Id == userId);
            user.DisabledAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        });

    private async Task<string> StepUpAsync(string email)
    {
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/step-up", new { password = TestHarness.Password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<StepUpDto>())!.Token;
    }

    // Resolves the real handler from a FRESH DI scope (its own AppDbContext, its
    // own execution strategy) and calls it directly — the "real handler, no
    // HTTP" shape CurrencyLockRaceTests established and ChangeUserRoleRaceTests
    // reuses.
    private async Task<Result> DisableAsync(
        Guid accountId, Guid targetUserId, Guid actingUserId, string? stepUpToken, string? reason = null)
    {
        using var scope = factory.Services.CreateScope();
        scope.ResolveTenantAndActor(accountId, actingUserId);
        var handler = scope.ServiceProvider.GetRequiredService<DisableUserHandler>();
        return await handler.HandleAsync(
            new DisableUserCommand(targetUserId, reason, stepUpToken), accountId, actingUserId, CancellationToken.None);
    }

    private async Task<Result> EnableAsync(
        Guid accountId, Guid targetUserId, Guid actingUserId, string? stepUpToken)
    {
        using var scope = factory.Services.CreateScope();
        scope.ResolveTenantAndActor(accountId, actingUserId);
        var handler = scope.ServiceProvider.GetRequiredService<EnableUserHandler>();
        return await handler.HandleAsync(
            new EnableUserCommand(targetUserId, stepUpToken), accountId, actingUserId, CancellationToken.None);
    }

    private Task<int> ActiveOwnerCountAsync(Guid accountId) =>
        factory.WithTenantScopeAsync(accountId, async db => await (
            from ur in db.UserRoles
            join r in db.Roles on ur.RoleId equals r.Id
            join u in db.Users on ur.UserId equals u.Id
            where r.Name == Cluckwork.Domain.Accounts.Roles.Owner
                && u.AccountId == accountId
                && u.DisabledAt == null
            select u.Id).CountAsync());

    private async Task<(AppDbContext Db, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction Tx, int Pid)>
        FenceAccountAsync(Guid accountId)
    {
        var tenant = new TenantContext();
        tenant.Resolve(accountId);
        var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(factory.ConnectionString).Options, tenant, new FlockScope());
        var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM "Accounts" WHERE "Id" = {accountId} FOR UPDATE""");
        return (db, tx, await db.BackendPidAsync());
    }

    // ---------- Guard boundaries unreachable over legitimate HTTP ----------

    [Fact]
    public async Task SoleOwner_SelfDisable_HitsLastOwnerGuard()
    {
        var sole = Unique("sole");
        var accountId = await SeedOwnerFarmAsync(sole);
        var ownerId = await UserIdAsync(accountId, sole);

        var result = await DisableAsync(accountId, ownerId, actingUserId: ownerId, await StepUpAsync(sole));

        Assert.True(result.IsFailure);
        Assert.Equal("Users.LastOwner", result.Error.Code);
        Assert.Equal(1, await ActiveOwnerCountAsync(accountId));
    }

    [Fact]
    public async Task AlreadyDisabledCoOwner_DoesNotCountTowardTheLastOwnerGuard()
    {
        // A naive survivor count sees "2 Owners" and lets the only WORKING
        // Owner be disabled. #355 already excludes DisabledAt from its own
        // count; this proves the disable path does the same.
        var active = Unique("active");
        var alreadyDisabled = Unique("disabled");
        var accountId = await SeedOwnerFarmAsync(active, alreadyDisabled);
        var activeId = await UserIdAsync(accountId, active);
        await ForceDisableAsync(accountId, await UserIdAsync(accountId, alreadyDisabled));

        var result = await DisableAsync(accountId, activeId, actingUserId: activeId, await StepUpAsync(active));

        Assert.True(result.IsFailure);
        Assert.Equal("Users.LastOwner", result.Error.Code);
        Assert.Equal(1, await ActiveOwnerCountAsync(accountId));
    }

    [Fact]
    public async Task DisablingANonOwner_IsNeverBlockedByTheLastOwnerGuard()
    {
        // The complement. Without it, a guard that returns Users.LastOwner for
        // EVERY target would pass both tests above and still be broken.
        var sole = Unique("sole");
        var worker = Unique("worker");
        var accountId = await SeedOwnerFarmAsync(sole);
        await factory.SeedUserAsync(accountId, worker, role: null);
        var workerId = await UserIdAsync(accountId, worker);

        var result = await DisableAsync(
            accountId, workerId, actingUserId: await UserIdAsync(accountId, sole), await StepUpAsync(sole));

        Assert.True(result.IsSuccess, $"disabling a worker must not hit the Owner guard: {result.Error}");
    }

    [Fact]
    public async Task DisabledActor_Is403_AndTheTargetIsNotDisabled()
    {
        // A disabled Owner keeps its Owner ROLE ROW — only authentication is
        // blocked — so the actor re-check must reject on DisabledAt, not on
        // effective role (#355 round-3 finding #2, same trap here).
        var active = Unique("active");
        var disabledActor = Unique("disabledactor");
        var accountId = await SeedOwnerFarmAsync(active, disabledActor);
        var activeId = await UserIdAsync(accountId, active);
        var disabledActorId = await UserIdAsync(accountId, disabledActor);
        var grant = await StepUpAsync(disabledActor);
        await ForceDisableAsync(accountId, disabledActorId);

        var result = await DisableAsync(accountId, activeId, actingUserId: disabledActorId, grant);

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.Forbidden", result.Error.Code);
        Assert.Null(await factory.WithTenantScopeAsync(accountId, async db =>
            await db.Users.Where(u => u.Id == activeId).Select(u => u.DisabledAt).SingleAsync()));
    }

    [Fact]
    public async Task DisabledActor_CannotEnableEither()
    {
        var active = Unique("active");
        var disabledActor = Unique("disabledactor");
        var target = Unique("target");
        var accountId = await SeedOwnerFarmAsync(active, disabledActor);
        await factory.SeedUserAsync(accountId, target, "Manager");
        var targetId = await UserIdAsync(accountId, target);
        var disabledActorId = await UserIdAsync(accountId, disabledActor);
        var grant = await StepUpAsync(disabledActor);
        await ForceDisableAsync(accountId, targetId);
        await ForceDisableAsync(accountId, disabledActorId);

        var result = await EnableAsync(accountId, targetId, actingUserId: disabledActorId, grant);

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.Forbidden", result.Error.Code);
        Assert.NotNull(await factory.WithTenantScopeAsync(accountId, async db =>
            await db.Users.Where(u => u.Id == targetId).Select(u => u.DisabledAt).SingleAsync()));
    }

    // ---------- The race the account lock exists for ----------

    [Fact]
    public async Task MutualDisableRace_TheLockSerializes_AndTheAccountKeepsAnActiveOwner()
    {
        // Without the account-wide lock both transactions read "2 active
        // Owners", both pass the guard, both commit against DIFFERENT rows —
        // and the farm is locked out of its own account.
        var a = Unique("a");
        var b = Unique("b");
        var accountId = await SeedOwnerFarmAsync(a, b);
        var aId = await UserIdAsync(accountId, a);
        var bId = await UserIdAsync(accountId, b);
        var grantA = await StepUpAsync(a);
        var grantB = await StepUpAsync(b);

        var (db, tx, pid) = await FenceAccountAsync(accountId);
        await using var _ = db;
        await using var __ = tx;

        // A disables B — launched first, so it heads the FIFO queue.
        var request1 = Task.Run(() => DisableAsync(accountId, bId, actingUserId: aId, grantA));
        Assert.True(await factory.WaitUntilDoneOrBlockedAsync(request1, pid),
            "the first disable must park on the account lock");

        // B disables A — queues behind request1 on the same fence.
        var request2 = Task.Run(() => DisableAsync(accountId, aId, actingUserId: bId, grantB));
        Assert.True(await factory.WaitUntilDoneOrBlockedAsync(request2, pid, minBlockedCount: 2),
            "the second disable must also queue behind the same fence");

        await tx.RollbackAsync(); // release without changing anything

        var result1 = await request1;
        var result2 = await request2;

        Assert.True(result1.IsSuccess, $"the first-queued disable must succeed: {result1.Error}");
        // request2's OWN acting user (B) was just disabled by request1 — the
        // actor re-check catches this before the last-Owner guard ever runs.
        Assert.True(result2.IsFailure, "the second disable must not also succeed");
        Assert.Equal("Auth.Forbidden", result2.Error.Code);
        Assert.Equal(1, await ActiveOwnerCountAsync(accountId));
    }

    [Fact]
    public async Task AsymmetricDisableRace_TwoLegitimateDisables_BothSucceed_AndTheActorSurvives()
    {
        // The complement to the mutual race, and NOT a guard test — stated
        // plainly because an earlier draft of this test claimed to prove the
        // survivor count is re-read INSIDE the lock, and it does not: with
        // three Owners and a single actor who is never themselves disabled,
        // both disables succeed whether that count is read inside the lock or
        // before it. What this DOES prove is that the unconditional lock
        // serializes two independently-legitimate concurrent operations rather
        // than corrupting or spuriously rejecting either — the same role
        // AsymmetricRace_BothActorsRemainOwners plays for #355.
        //
        // Reaching the last-Owner guard through a genuine race requires two
        // actors disabling EACH OTHER, which is the test above.
        var actor = Unique("actor");
        var b = Unique("b");
        var c = Unique("c");
        var accountId = await SeedOwnerFarmAsync(actor, b, c);
        var bId = await UserIdAsync(accountId, b);
        var cId = await UserIdAsync(accountId, c);
        var grant = await StepUpAsync(actor);
        var actorId = await UserIdAsync(accountId, actor);
        var secondGrant = await StepUpAsync(actor);

        var (db, tx, pid) = await FenceAccountAsync(accountId);
        await using var _ = db;
        await using var __ = tx;

        var disableB = Task.Run(() => DisableAsync(accountId, bId, actingUserId: actorId, grant));
        Assert.True(await factory.WaitUntilDoneOrBlockedAsync(disableB, pid));

        var disableC = Task.Run(() => DisableAsync(accountId, cId, actingUserId: actorId, secondGrant));
        Assert.True(await factory.WaitUntilDoneOrBlockedAsync(disableC, pid, minBlockedCount: 2));

        await tx.RollbackAsync();

        Assert.True((await disableB).IsSuccess);
        Assert.True((await disableC).IsSuccess);
        // Three Owners, two disabled, the actor still active — legitimate.
        Assert.Equal(1, await ActiveOwnerCountAsync(accountId));
    }

    // ---------- A refresh in flight across a disable ----------

    [Fact]
    public async Task RefreshInFlightAcrossADisable_LeavesNoUsableCredential()
    {
        // Bulk revocation alone is not sufficient: a concurrent refresh can
        // insert a live CHILD token after the revoke commits (design-doc defect
        // #7). What actually closes it is IssuedEpoch — a child minted from a
        // pre-disable read carries the OLD epoch and is dead on arrival.
        //
        // The assertion is therefore on the invariant rather than on one
        // interleaving: whichever way the two transactions order, the target
        // must hold no usable credential afterwards, and every surviving
        // un-revoked row must carry a stale epoch.
        var owner = Unique("owner");
        var target = Unique("target");
        var accountId = await SeedOwnerFarmAsync(owner);
        await factory.SeedUserAsync(accountId, target, "Manager");
        var ownerId = await UserIdAsync(accountId, owner);
        var targetId = await UserIdAsync(accountId, target);
        var grant = await StepUpAsync(owner);
        var session = await factory.LoginAsync(target);

        var (db, tx, pid) = await FenceAccountAsync(accountId);
        await using var _ = db;
        await using var __ = tx;

        var disable = Task.Run(() => DisableAsync(accountId, targetId, actingUserId: ownerId, grant));
        Assert.True(await factory.WaitUntilDoneOrBlockedAsync(disable, pid),
            "the disable must park on the account lock");

        // The refresh runs while the disable is parked — it reads the target's
        // pre-disable state and mints its child from it.
        var refresh = await factory.CreateClient().PostRefreshAsync(session.RefreshToken, expectedAccount: accountId.ToString());

        await tx.RollbackAsync();
        Assert.True((await disable).IsSuccess, "the disable must still complete");

        // UNCONDITIONAL. The disable is provably parked on the fence before the
        // refresh is issued, so the refresh succeeding is deterministic, not a
        // maybe — an earlier draft wrapped everything below in
        // `if (refresh.StatusCode == OK)` and would therefore have asserted
        // NOTHING had the refresh started failing for an unrelated reason.
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        var child = (await refresh.Content.ReadFromJsonAsync<TokenPairDto>())!;

        // The child was minted from a pre-disable read, so it must be dead on
        // both faces: the access token on its very next request, and the
        // refresh token on its next rotation.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await factory.CreateAuthedClient(child.AccessToken).GetAsync("/api/v1/flocks")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await factory.CreateClient().PostRefreshAsync(child.RefreshToken, expectedAccount: accountId.ToString())).StatusCode);

        // Belt and braces on the row state. Assert.All over an EMPTY sequence
        // is vacuously true, and in this interleaving the disable's bulk revoke
        // runs after the refresh commits and so revokes the child too — leaving
        // exactly zero survivors. Naming that here rather than letting a
        // trivially-true assertion read as a proof; the falsifiable content of
        // this test is the two 401s above.
        var currentEpoch = await factory.WithTenantScopeAsync(accountId, async d =>
            await d.Users.Where(u => u.Id == targetId).Select(u => u.CredentialEpoch).SingleAsync());
        var survivors = await factory.WithTenantScopeAsync(accountId, async d => await d.RefreshTokens
            .Where(t => t.UserId == targetId && t.RevokedAt == null)
            .Select(t => t.IssuedEpoch)
            .ToListAsync());
        Assert.All(survivors, epoch => Assert.True(epoch < currentEpoch,
            $"an un-revoked refresh token carries epoch {epoch}, still valid against {currentEpoch}"));
    }

    private sealed record TokenPairDto(string AccessToken, string RefreshToken, DateTimeOffset AccessTokenExpiry);

    // ---------- Step-up issuance racing a disable ----------

    [Fact]
    public async Task StepUpIssuedWhileADisableIsQueued_IsNotSpendableAfterAReEnable()
    {
        // The grant is minted from the target's PRE-disable SecurityStamp while
        // the disable sits on the account lock. Re-enabling then clears
        // DisabledAt — so if the disable had not rotated the stamp, that grant
        // would be spendable against a fully restored account.
        var owner = Unique("owner");
        var target = Unique("target");
        var accountId = await SeedOwnerFarmAsync(owner);
        await factory.SeedUserAsync(accountId, target, "Manager");
        var ownerId = await UserIdAsync(accountId, owner);
        var targetId = await UserIdAsync(accountId, target);
        var disableGrant = await StepUpAsync(owner);
        var enableGrant = await StepUpAsync(owner);

        var (db, tx, pid) = await FenceAccountAsync(accountId);
        await using var _ = db;
        await using var __ = tx;

        var disable = Task.Run(() => DisableAsync(accountId, targetId, actingUserId: ownerId, disableGrant));
        Assert.True(await factory.WaitUntilDoneOrBlockedAsync(disable, pid));

        // Minted while the disable is parked — the target is still active here.
        var targetGrant = await StepUpAsync(target);

        await tx.RollbackAsync();
        Assert.True((await disable).IsSuccess);
        Assert.True((await EnableAsync(accountId, targetId, actingUserId: ownerId, enableGrant)).IsSuccess);

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(accountId);
        var stepUp = scope.ServiceProvider.GetRequiredService<IStepUpGrantService>();

        Assert.True((await stepUp.ValidateAsync(accountId, targetId, targetGrant, CancellationToken.None)).IsFailure,
            "a grant minted before the disable must not survive the re-enable");
    }

    // ---------- Identity-level concurrency conflict ----------

    [Fact]
    public async Task ConcurrentStampChange_DuringTheDisableItself_Is409()
    {
        // Identity's UserStore.UpdateAsync swallows a concurrency loss into a
        // FAILED IdentityResult rather than throwing, so a race landing inside
        // UpdateSecurityStampAsync must be inspected for a ConcurrencyFailure
        // code and mapped to Users.Conflict — separately from whatever the
        // outer SaveChangesAsync catch handles. Constructed deterministically
        // rather than by timing: fence the TARGET USER row, let the disable's
        // own UPDATE queue behind it, then commit a different ConcurrencyStamp
        // so the queued UPDATE's WHERE matches zero rows.
        var owner = Unique("owner");
        var target = Unique("target");
        var accountId = await SeedOwnerFarmAsync(owner);
        await factory.SeedUserAsync(accountId, target, "Manager");
        var ownerId = await UserIdAsync(accountId, owner);
        var targetId = await UserIdAsync(accountId, target);
        var grant = await StepUpAsync(owner);

        var tenant = new TenantContext();
        tenant.Resolve(accountId);
        await using var fenceDb = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(factory.ConnectionString).Options, tenant, new FlockScope());
        await using var fenceTx = await fenceDb.Database.BeginTransactionAsync();
        await fenceDb.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM "AspNetUsers" WHERE "Id" = {targetId} FOR UPDATE""");
        var fencePid = await fenceDb.BackendPidAsync();

        var disable = Task.Run(() => DisableAsync(accountId, targetId, actingUserId: ownerId, grant));
        Assert.True(await factory.WaitUntilDoneOrBlockedAsync(disable, fencePid),
            "the disable's own UPDATE must park behind the user-row fence");

        await fenceDb.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "AspNetUsers" SET "ConcurrencyStamp" = {Guid.NewGuid().ToString()} WHERE "Id" = {targetId}""");
        await fenceTx.CommitAsync();

        var result = await disable;

        Assert.True(result.IsFailure);
        Assert.Equal("Users.Conflict", result.Error.Code);
        Assert.Null(await factory.WithTenantScopeAsync(accountId, async d =>
            await d.Users.Where(u => u.Id == targetId).Select(u => u.DisabledAt).SingleAsync()));
    }

    [Fact]
    public async Task ConcurrentStampChange_DuringTheEnableItself_Is409()
    {
        // The enable side of the same hazard, and the reason EnableUserAsync
        // rotates the stamp at all. A plain SaveChangesAsync leaves
        // ConcurrencyStamp untouched, so a concurrent full-entity Identity
        // write (SetUserPassword spends the whole PBKDF2 window between its
        // read and its write) would still match the stamp it read BEFORE the
        // enable and quietly write DisabledAt back — a 204 and a User.Enabled
        // audit row for an enable that never survived. With the rotation, that
        // stale write loses its CAS and the loser is told so.
        var owner = Unique("owner");
        var target = Unique("target");
        var accountId = await SeedOwnerFarmAsync(owner);
        await factory.SeedUserAsync(accountId, target, "Manager");
        var ownerId = await UserIdAsync(accountId, owner);
        var targetId = await UserIdAsync(accountId, target);
        var grant = await StepUpAsync(owner);
        await ForceDisableAsync(accountId, targetId);

        var tenant = new TenantContext();
        tenant.Resolve(accountId);
        await using var fenceDb = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(factory.ConnectionString).Options, tenant, new FlockScope());
        await using var fenceTx = await fenceDb.Database.BeginTransactionAsync();
        await fenceDb.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM "AspNetUsers" WHERE "Id" = {targetId} FOR UPDATE""");
        var fencePid = await fenceDb.BackendPidAsync();

        var enable = Task.Run(() => EnableAsync(accountId, targetId, actingUserId: ownerId, grant));
        Assert.True(await factory.WaitUntilDoneOrBlockedAsync(enable, fencePid),
            "the enable's own UPDATE must park behind the user-row fence");

        await fenceDb.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "AspNetUsers" SET "ConcurrencyStamp" = {Guid.NewGuid().ToString()} WHERE "Id" = {targetId}""");
        await fenceTx.CommitAsync();

        var result = await enable;

        Assert.True(result.IsFailure, "an enable that lost the CAS must not report success");
        Assert.Equal("Users.Conflict", result.Error.Code);
        // Still disabled — the losing enable changed nothing.
        Assert.NotNull(await factory.WithTenantScopeAsync(accountId, async d =>
            await d.Users.Where(u => u.Id == targetId).Select(u => u.DisabledAt).SingleAsync()));
    }

    [Fact]
    public async Task Enable_ParksOnTheAccountLock_AndCompletesOnceItIsReleased()
    {
        // Named for what it actually does: no disable runs here, so it does not
        // demonstrate serialisation against one — it demonstrates that the
        // enable path TAKES the lock at all, which is the precondition for any
        // serialisation claim. Renamed after review caught the original name
        // ("SoItSerializesAgainstADisable") promising the stronger thing.
        //
        // Pins the lock on the ENABLE path specifically. Without it, deleting
        // GetCurrentLockedAsync from EnableUserAsync is green across every other
        // test in both new files — and the enable path is the one with a real
        // concurrency gap (see ConcurrentStampChange_DuringTheEnableItself_Is409).
        // WaitUntilDoneOrBlockedAsync returns FALSE when the task completes
        // instead of blocking, so an unlocked enable fails this outright.
        var owner = Unique("owner");
        var target = Unique("target");
        var accountId = await SeedOwnerFarmAsync(owner);
        await factory.SeedUserAsync(accountId, target, "Manager");
        var ownerId = await UserIdAsync(accountId, owner);
        var targetId = await UserIdAsync(accountId, target);
        var grant = await StepUpAsync(owner);
        await ForceDisableAsync(accountId, targetId);

        var (db, tx, pid) = await FenceAccountAsync(accountId);
        await using var _ = db;
        await using var __ = tx;

        var enable = Task.Run(() => EnableAsync(accountId, targetId, actingUserId: ownerId, grant));
        Assert.True(await factory.WaitUntilDoneOrBlockedAsync(enable, pid),
            "the enable must park on the account lock, not sail past it");

        await tx.RollbackAsync();

        Assert.True((await enable).IsSuccess, $"and then complete once released: {(await enable).Error}");
        Assert.Null(await factory.WithTenantScopeAsync(accountId, async d =>
            await d.Users.Where(u => u.Id == targetId).Select(u => u.DisabledAt).SingleAsync()));
    }

    [Fact]
    public async Task PasswordResetLosingTheCas_Is409_NotAPasswordRejection()
    {
        // Collateral of #356's enable-side stamp rotation, and the reason
        // ResetPasswordAndRevokeAsync now separates a lost CAS from a bad
        // password. That rotation exists precisely so a concurrent
        // SetUserPassword LOSES — and Identity reports a lost CAS as a FAILED
        // IdentityResult, not a throw. Mapped naively it surfaces as
        // Users.PasswordRejected: a 422 whose only actionable reading is
        // "choose a stronger password", for a password that was never the
        // problem. Constructed deterministically with the same user-row fence
        // the disable/enable conflict tests use, rather than by racing a real
        // enable and hoping for the interleaving.
        var owner = Unique("owner");
        var target = Unique("target");
        var accountId = await SeedOwnerFarmAsync(owner);
        await factory.SeedUserAsync(accountId, target, "Manager");
        var targetId = await UserIdAsync(accountId, target);

        var tenant = new TenantContext();
        tenant.Resolve(accountId);
        await using var fenceDb = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(factory.ConnectionString).Options, tenant, new FlockScope());
        await using var fenceTx = await fenceDb.Database.BeginTransactionAsync();
        await fenceDb.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM "AspNetUsers" WHERE "Id" = {targetId} FOR UPDATE""");
        var fencePid = await fenceDb.BackendPidAsync();

        var reset = Task.Run(async () =>
        {
            using var scope = factory.Services.CreateScope();
            scope.ResolveTenantAndActor(accountId);
            var identity = scope.ServiceProvider.GetRequiredService<IIdentityProvider>();
            return await identity.SetUserPasswordAsync(
                accountId, targetId, $"Aa1!{Guid.NewGuid():N}", CancellationToken.None);
        });
        Assert.True(await factory.WaitUntilDoneOrBlockedAsync(reset, fencePid),
            "the password reset's own UPDATE must park behind the user-row fence");

        await fenceDb.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "AspNetUsers" SET "ConcurrencyStamp" = {Guid.NewGuid().ToString()} WHERE "Id" = {targetId}""");
        await fenceTx.CommitAsync();

        var result = await reset;

        Assert.True(result.IsFailure);
        Assert.Equal("Users.Conflict", result.Error.Code);
    }

    [Fact]
    public async Task PasswordResetLosingTheCas_OverHttp_Is409_NotA422()
    {
        // The endpoint half of the finding above (codex, #492 round 2): the
        // direct-DI test pins the ERROR CODE, but SetUserPassword's endpoint
        // mapping had no ".Conflict" branch, so over HTTP the same loss
        // surfaced as a 422 the SPA reads as "password rejected". #360 —
        // every administrative reset now requires a step-up grant, so the
        // request carries a fresh Owner grant to reach the concurrency seam;
        // the 409 is still purely the concurrency mapping.
        var owner = Unique("owner");
        var target = Unique("target");
        var accountId = await SeedOwnerFarmAsync(owner);
        await factory.SeedUserAsync(accountId, target, "Manager");
        var targetId = await UserIdAsync(accountId, target);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(owner));

        var tenant = new TenantContext();
        tenant.Resolve(accountId);
        await using var fenceDb = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(factory.ConnectionString).Options, tenant, new FlockScope());
        await using var fenceTx = await fenceDb.Database.BeginTransactionAsync();
        await fenceDb.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM "AspNetUsers" WHERE "Id" = {targetId} FOR UPDATE""");
        var fencePid = await fenceDb.BackendPidAsync();

        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/users/{targetId}/password")
        {
            Content = JsonContent.Create(new { newPassword = $"Aa1!{Guid.NewGuid():N}" }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.Add(AuthEndpoints.StepUpHeaderName, await StepUpAsync(owner));
        var reset = Task.Run(() => client.SendAsync(request));
        Assert.True(await factory.WaitUntilDoneOrBlockedAsync(reset, fencePid),
            "the reset's UPDATE must park behind the user-row fence");

        await fenceDb.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "AspNetUsers" SET "ConcurrencyStamp" = {Guid.NewGuid().ToString()} WHERE "Id" = {targetId}""");
        await fenceTx.CommitAsync();

        var response = await reset;

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Users.Conflict",
            (await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>())!.Title);
    }

    [Fact]
    public async Task StaleOwnerActor_OverHttp_Is403_NotA422()
    {
        // The endpoint's `"Auth.Forbidden" => 403` mapping is otherwise
        // unpinned: every test that produces AppError.Forbidden() from
        // RequireActiveOwnerAsync calls the handler directly through DI and
        // asserts the error CODE, while NonOwnerCaller_Is403 never reaches the
        // handler at all (the group's OwnerOnly policy stops it first). So a
        // typo in that literal would silently downgrade a disabled-Owner
        // rejection to 422, and nothing would notice. This drives it over real
        // HTTP: the caller is authenticated and past authorization, and only
        // becomes unauthorized while queued on the lock.
        var actor = Unique("actor");
        var coOwner = Unique("coowner");
        var target = Unique("target");
        var accountId = await SeedOwnerFarmAsync(actor, coOwner);
        await factory.SeedUserAsync(accountId, target, "Manager");
        var actorId = await UserIdAsync(accountId, actor);
        var targetId = await UserIdAsync(accountId, target);

        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(actor));
        var stepUpToken = await StepUpAsync(actor);

        var (db, tx, pid) = await FenceAccountAsync(accountId);
        await using var _ = db;
        await using var __ = tx;

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/users/{targetId}/disable")
        {
            Content = JsonContent.Create(new { reason = (string?)null }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.Add(Cluckwork.Api.Endpoints.Auth.AuthEndpoints.StepUpHeaderName, stepUpToken);
        var inFlight = Task.Run(() => client.SendAsync(request));
        Assert.True(await factory.WaitUntilDoneOrBlockedAsync(inFlight, pid),
            "the request must park on the account lock after passing authorization");

        // The actor loses their access while their own request is queued.
        await ForceDisableAsync(accountId, actorId);

        await tx.RollbackAsync();

        var response = await inFlight;
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Auth.Forbidden",
            (await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>())!.Title);
        Assert.Null(await factory.WithTenantScopeAsync(accountId, async d =>
            await d.Users.Where(u => u.Id == targetId).Select(u => u.DisabledAt).SingleAsync()));
    }
}
