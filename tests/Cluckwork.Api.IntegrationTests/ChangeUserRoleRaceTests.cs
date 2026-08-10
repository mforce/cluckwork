namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Users.ChangeUserRole;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #355 — the Users.LastOwner guard and the actor-still-Owner re-check are both
// evaluated inside IdentityProvider.ChangeUserRoleAsync's account-locked
// transaction. Two things make several of their branches unreachable through
// a legitimate, non-racing, distinct HTTP actor:
//
//   - the /users group is Owner-only, so only an Owner can ever call this;
//   - ChangeUserRoleTests' self-target guard blocks a sole Owner targeting
//     themselves at the API layer, before validation even runs.
//
// So a sole Owner can never LEGITIMATELY, sequentially, via HTTP, hit
// Users.LastOwner with a distinct actor — the only two ways to observe it are
// (a) a genuine concurrent race, or (b) resolving ChangeUserRoleHandler
// directly via DI and calling it with actingUserId == userId, bypassing the
// endpoint layer's self-block on purpose. This file does both, following the
// exact pattern CurrencyLockRaceTests already established for #162: a raw
// FOR UPDATE fence held on a hand-built AppDbContext (not factory.Services —
// that context carries EnableRetryOnFailure, incompatible with this file's
// need for precise, hand-held control of a transaction across several
// separate steps), with REAL handlers resolved from DI on the other side.
[Collection(IntegrationCollection.Name)]
public sealed class ChangeUserRoleRaceTests(CluckworkWebApplicationFactory factory)
{
    private sealed record StepUpDto(string Token, DateTimeOffset ExpiresAt);

    // AspNetUsers' UserNameIndex is a GLOBAL unique index, not account-scoped
    // — every seeded email in this file must be unique per TEST, not just
    // per account, or a later test collides with an earlier one's row.
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

    private async Task DisableAsync(Guid accountId, Guid userId) =>
        await factory.WithTenantScopeAsync(accountId, async db =>
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

    // Resolves ChangeUserRoleHandler from a FRESH DI scope (its own AppDbContext,
    // its own EnableRetryOnFailure execution strategy) and calls it directly —
    // the same "real handler, no HTTP" shape CurrencyLockRaceTests uses for its
    // racing tasks.
    private async Task<Result> InvokeAsync(
        Guid accountId, Guid targetUserId, string role, Guid actingUserId, string? stepUpToken = null)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(accountId);
        var handler = scope.ServiceProvider.GetRequiredService<ChangeUserRoleHandler>();
        return await handler.HandleAsync(
            new ChangeUserRoleCommand(targetUserId, role, stepUpToken), accountId, actingUserId, CancellationToken.None);
    }

    // ---------- Direct-provider guard boundary (unreachable via legitimate HTTP) ----------

    [Fact]
    public async Task SoleOwner_SelfDemotion_HitsLastOwnerGuard()
    {
        var sole = Unique("sole");
        var accountId = await SeedOwnerFarmAsync(sole);
        var ownerId = await UserIdAsync(accountId, sole);

        var result = await InvokeAsync(accountId, ownerId, "Manager", actingUserId: ownerId);

        Assert.True(result.IsFailure);
        Assert.Equal("Users.LastOwner", result.Error.Code);
    }

    [Fact]
    public async Task DisabledOwner_DoesNotCountTowardTheLastOwnerGuard()
    {
        var active = Unique("active");
        var disabled = Unique("disabled");
        var accountId = await SeedOwnerFarmAsync(active, disabled);
        var activeId = await UserIdAsync(accountId, active);
        var disabledId = await UserIdAsync(accountId, disabled);
        await DisableAsync(accountId, disabledId);

        // Demoting the ACTIVE owner, acting as themselves (bypassing the
        // endpoint's self-block) — the disabled co-owner must not count as a
        // survivor.
        var result = await InvokeAsync(accountId, activeId, "Manager", actingUserId: activeId);

        Assert.True(result.IsFailure);
        Assert.Equal("Users.LastOwner", result.Error.Code);
    }

    [Fact]
    public async Task DisabledActor_Is403_AndTheTargetIsNotMutated()
    {
        // #355 round-3 finding #2 — a disabled Owner keeps its Owner ROLE ROW
        // (only auth is blocked); the actor re-check must reject on
        // DisabledAt, not just on effective role.
        var active = Unique("active");
        var disabledActor = Unique("disabledactor");
        var accountId = await SeedOwnerFarmAsync(active, disabledActor);
        var activeId = await UserIdAsync(accountId, active);
        var disabledActorId = await UserIdAsync(accountId, disabledActor);
        await DisableAsync(accountId, disabledActorId);

        var result = await InvokeAsync(accountId, activeId, "Manager", actingUserId: disabledActorId);

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.Forbidden", result.Error.Code);
        var stillOwner = await factory.WithTenantScopeAsync(accountId, async db =>
            await (from ur in db.UserRoles
                   join r in db.Roles on ur.RoleId equals r.Id
                   where ur.UserId == activeId
                   select r.Name).ToListAsync());
        Assert.Equal(["Admin"], stillOwner);
    }

    // ---------- Account-lock fence helpers (mirrors CurrencyLockRaceTests) ----------

    private async Task<(AppDbContext Db, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction Tx, int Pid)>
        FenceAccountAsync(Guid accountId)
    {
        var tenant = new TenantContext();
        tenant.Resolve(accountId);
        var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(factory.ConnectionString).Options, tenant);
        var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM "Accounts" WHERE "Id" = {accountId} FOR UPDATE""");
        return (db, tx, await db.BackendPidAsync());
    }

    [Fact]
    public async Task MutualDemotionRace_TheLockSerializes_FirstSucceedsSecondFailsAsStaleActor()
    {
        var a = Unique("a");
        var b = Unique("b");
        var accountId = await SeedOwnerFarmAsync(a, b);
        var aId = await UserIdAsync(accountId, a);
        var bId = await UserIdAsync(accountId, b);

        var (db, tx, pid) = await FenceAccountAsync(accountId);
        await using var _ = db;
        await using var __ = tx;

        // A demotes B — launched first, so it heads the FIFO queue.
        var request1 = Task.Run(() => InvokeAsync(accountId, bId, "Manager", actingUserId: aId));
        Assert.True(await factory.WaitUntilDoneOrBlockedAsync(request1, pid),
            "the first demotion must park on the account lock");

        // B demotes A — launched second, queues behind request1.
        var request2 = Task.Run(() => InvokeAsync(accountId, aId, "Manager", actingUserId: bId));
        Assert.True(await factory.WaitUntilDoneOrBlockedAsync(request2, pid, minBlockedCount: 2),
            "the second demotion must also queue up behind the same fence");

        await tx.RollbackAsync(); // releases the fence without changing anything

        var result1 = await request1;
        var result2 = await request2;

        Assert.True(result1.IsSuccess, $"the first-queued demotion must succeed: {result1.Error}");
        // request2's OWN acting user (B) was just demoted by request1 — the
        // actor re-check must catch this, not the last-Owner guard (which
        // never runs on this path).
        Assert.True(result2.IsFailure);
        Assert.Equal("Auth.Forbidden", result2.Error.Code);
    }

    [Fact]
    public async Task AsymmetricRace_BothActorsRemainOwners_BothOperationsSucceed()
    {
        // #355 round-3 finding #4 — this proves the lock correctly SERIALIZES
        // two independently-legitimate concurrent operations rather than
        // corrupting either; it is not a rejection test like the mutual-
        // demotion race above, because A (the sole actor here) is never
        // demoted by either operation.
        var a = Unique("a");
        var b = Unique("b");
        var c = Unique("c");
        var accountId = await SeedOwnerFarmAsync(a, b);
        await factory.SeedUserAsync(accountId, c, "Manager");
        var aId = await UserIdAsync(accountId, a);
        var bId = await UserIdAsync(accountId, b);
        var cId = await UserIdAsync(accountId, c);
        var stepUp = await StepUpAsync(a);

        var (db, tx, pid) = await FenceAccountAsync(accountId);
        await using var _ = db;
        await using var __ = tx;

        var demoteB = Task.Run(() => InvokeAsync(accountId, bId, "Manager", actingUserId: aId));
        Assert.True(await factory.WaitUntilDoneOrBlockedAsync(demoteB, pid));

        var promoteC = Task.Run(() => InvokeAsync(accountId, cId, "Admin", actingUserId: aId, stepUp));
        Assert.True(await factory.WaitUntilDoneOrBlockedAsync(promoteC, pid, minBlockedCount: 2));

        await tx.RollbackAsync();

        var demoteResult = await demoteB;
        var promoteResult = await promoteC;

        Assert.True(demoteResult.IsSuccess, $"demoting B must succeed: {demoteResult.Error}");
        Assert.True(promoteResult.IsSuccess, $"promoting C must succeed: {promoteResult.Error}");
    }

    [Fact]
    public async Task StaleActorRace_BsQueuedPromotionFailsAfterBeingDemoted_AndCIsNotPromoted()
    {
        var a = Unique("a");
        var b = Unique("b");
        var c = Unique("c");
        var accountId = await SeedOwnerFarmAsync(a, b);
        await factory.SeedUserAsync(accountId, c, "Manager");
        var aId = await UserIdAsync(accountId, a);
        var bId = await UserIdAsync(accountId, b);
        var cId = await UserIdAsync(accountId, c);
        var stepUpForB = await StepUpAsync(b);

        var (db, tx, pid) = await FenceAccountAsync(accountId);
        await using var _ = db;
        await using var __ = tx;

        // A demotes B — launched first, heads the queue.
        var demoteB = Task.Run(() => InvokeAsync(accountId, bId, "Manager", actingUserId: aId));
        Assert.True(await factory.WaitUntilDoneOrBlockedAsync(demoteB, pid));

        // B, still believing themselves Owner, tries to promote C.
        var promoteC = Task.Run(() => InvokeAsync(accountId, cId, "Admin", actingUserId: bId, stepUpForB));
        Assert.True(await factory.WaitUntilDoneOrBlockedAsync(promoteC, pid, minBlockedCount: 2));

        await tx.RollbackAsync();

        var demoteResult = await demoteB;
        var promoteResult = await promoteC;

        Assert.True(demoteResult.IsSuccess, $"A's demotion of B must succeed: {demoteResult.Error}");
        Assert.True(promoteResult.IsFailure, "B's promotion must fail — B was just demoted");
        Assert.Equal("Auth.Forbidden", promoteResult.Error.Code);
        var cRole = await factory.WithTenantScopeAsync(accountId, async db2 =>
            await (from ur in db2.UserRoles
                   join r in db2.Roles on ur.RoleId equals r.Id
                   where ur.UserId == cId
                   select r.Name).ToListAsync());
        Assert.Equal(["Manager"], cRole); // never promoted
    }

    // ---------- Stale-tracked-actor fix (codex review, PR #475 round-2) ----------

    [Fact]
    public async Task StaleTrackedActorFix_ActorDisabledWhilePromotionQueued_IsForbidden_NotStaleSuccess()
    {
        // StepUpGrantService.ValidateAsync tracks the ACTOR's ApplicationUser
        // (via userManager.FindByIdAsync) on the SAME scoped DbContext the
        // handler goes on to use for the actor re-check inside the account
        // lock. EF's identity map means a later tracked query for the same PK
        // would silently return that CACHED (pre-lock) instance instead of a
        // fresh row — so if the actor is disabled AFTER their grant is
        // validated but BEFORE the lock is acquired, a stale re-check would
        // still see DisabledAt == null and let the promotion through.
        var owner = Unique("owner");
        var promoter = Unique("promoter");
        var target = Unique("target");
        var accountId = await SeedOwnerFarmAsync(owner, promoter);
        await factory.SeedUserAsync(accountId, target, "Manager");
        var promoterId = await UserIdAsync(accountId, promoter);
        var targetId = await UserIdAsync(accountId, target);
        var stepUpForPromoter = await StepUpAsync(promoter);

        var (db, tx, pid) = await FenceAccountAsync(accountId);
        await using var _ = db;
        await using var __ = tx;

        // The promoter's own request: consumes their step-up grant (tracking
        // their ApplicationUser row on the handler's DbContext), THEN blocks
        // on the fenced account lock.
        var promote = Task.Run(() =>
            InvokeAsync(accountId, targetId, "Admin", actingUserId: promoterId, stepUpForPromoter));
        Assert.True(await factory.WaitUntilDoneOrBlockedAsync(promote, pid),
            "the promotion must park on the account lock after consuming the step-up grant");

        // While it's queued, disable the promoter via a completely separate
        // connection/DbContext — the promoter's own row isn't locked by the
        // queued transaction yet (it's still parked on the ACCOUNT row), so
        // this must not collide with the fence.
        await DisableAsync(accountId, promoterId);

        await tx.RollbackAsync();

        var result = await promote;

        Assert.True(result.IsFailure, "a disabled actor's queued promotion must not succeed");
        Assert.Equal("Auth.Forbidden", result.Error.Code);
        var targetRole = await factory.WithTenantScopeAsync(accountId, async db2 =>
            await (from ur in db2.UserRoles
                   join r in db2.Roles on ur.RoleId equals r.Id
                   where ur.UserId == targetId
                   select r.Name).ToListAsync());
        Assert.Equal(["Manager"], targetRole); // never promoted
    }

    // ---------- IdentityResult-level concurrency conflict (#355 round-2/3 finding) ----------

    // Identity's UserStore.UpdateAsync swallows a concurrency loss into a
    // FAILED IdentityResult rather than throwing (this exact codebase's own
    // documented prior incident — AccountLockout.cs's comment on
    // RecordFailedAccessAsync/ResetFailedAccessCountAsync). So a race landing
    // inside RemoveFromRolesAsync/AddToRoleAsync must be inspected for a
    // ConcurrencyFailure-coded IdentityResult and mapped to 409, separately
    // from whatever the outer SaveChangesAsync catch handles. Constructed
    // deterministically (not by timing luck): fence the TARGET USER row (not
    // the account row) FOR UPDATE, let the role-change queue behind it inside
    // AddToRoleAsync's own internal UPDATE, then commit a DIFFERENT
    // ConcurrencyStamp value from the fence — the queued UPDATE's WHERE
    // clause (bound to the value read before the fence committed) then
    // matches zero rows, which is exactly what Identity's own concurrency
    // detection is watching for.
    [Fact]
    public async Task ConcurrentStampChange_DuringTheRoleMutationItself_Is409()
    {
        var a = Unique("a");
        var b = Unique("b");
        var accountId = await SeedOwnerFarmAsync(a, b);
        var aId = await UserIdAsync(accountId, a);
        var bId = await UserIdAsync(accountId, b);

        var tenant = new TenantContext();
        tenant.Resolve(accountId);
        await using var fenceDb = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(factory.ConnectionString).Options, tenant);
        await using var fenceTx = await fenceDb.Database.BeginTransactionAsync();
        await fenceDb.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM "AspNetUsers" WHERE "Id" = {bId} FOR UPDATE""");
        var fencePid = await fenceDb.BackendPidAsync();

        // A demotes B — proceeds past the (unrelated) account lock, reads B's
        // row via a plain SELECT (unaffected by the fence's row lock), then
        // its internal AddToRoleAsync/RemoveFromRoleAsync UPDATE parks behind
        // the fence.
        var demoteB = Task.Run(() => InvokeAsync(accountId, bId, "Manager", actingUserId: aId));
        Assert.True(await factory.WaitUntilDoneOrBlockedAsync(demoteB, fencePid),
            "the role mutation's own UPDATE must park behind the user-row fence");

        // The fence changes the stamp out from under the queued UPDATE, then
        // releases by committing.
        await fenceDb.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "AspNetUsers" SET "ConcurrencyStamp" = {Guid.NewGuid().ToString()} WHERE "Id" = {bId}""");
        await fenceTx.CommitAsync();

        var result = await demoteB;

        Assert.True(result.IsFailure);
        Assert.Equal("Users.Conflict", result.Error.Code);
        // Unmutated: B is still whatever role it started as (the failed
        // UPDATE affected zero rows).
        var bRole = await factory.WithTenantScopeAsync(accountId, async db2 =>
            await (from ur in db2.UserRoles
                   join r in db2.Roles on ur.RoleId equals r.Id
                   where ur.UserId == bId
                   select r.Name).ToListAsync());
        Assert.Equal(["Admin"], bRole);
    }
}
