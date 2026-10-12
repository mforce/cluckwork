using Cluckwork.Application.Common;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

public sealed partial class IdentityProvider
{
    // #356 — deliberately NOT the role path's bare "Reload and retry." wording.
    // Disable/enable require step-up UNCONDITIONALLY, and the grant is
    // single-use and is spent BEFORE the account lock is taken (see
    // DisableUserHandler's note), so by the time this conflict is raised the
    // caller's password proof is already gone and resubmitting the identical
    // request answers 403, not success. The copy says so rather than promising
    // a retry that cannot work.
    // Used ONLY by the disable/enable paths, where that sentence is always
    // true. The password-reset paths use ResetConcurrencyConflict in
    // IdentityProvider.Passwords.cs.
    private static Error ConcurrencyConflict() => Error.Conflict(
        "Users.Conflict",
        "The user was modified by another request, and your password confirmation has already been used. "
        + "Reload, confirm your password again, and retry.");

    // #356 — disable a user. See IIdentityProvider for the full contract; the
    // load-bearing part is that this is NOT just a flag write. The flag is what
    // CredentialEpochMiddleware reads today (#364); the EPOCH BUMP is what
    // makes a later re-enable safe, because it leaves every pre-disable access
    // token permanently behind the account's current epoch.
    //
    // AmbientTransaction shape mirrors ChangeUserRoleAsync: it joins
    // IdempotencyMiddleware's ambient request transaction when one is open, and
    // on the owned path (a non-HTTP caller) is NOT retried — a replay would
    // re-run the mutation and re-append the audit row (#269).
    public Task<Result> DisableUserAsync(
        Guid accountId, Guid userId, Guid actingUserId, string? reason,
        CancellationToken ct = default) =>
        AmbientTransaction.RunAsync(db.Database, async (transaction, token) =>
        {
            // The account-wide lock, taken UNCONDITIONALLY — even for a target
            // who could never affect the Owner count. The last-active-Owner
            // guard is NOT race-safe on ConcurrencyStamp: two Owners disabling
            // each other touch DIFFERENT rows and share no concurrency token,
            // so both would read "two active Owners" and both would commit.
            // The result is VALIDATED, not discarded: GetCurrentLockedAsync
            // resolves from the ambient TenantContext, not from the accountId
            // parameter, so a caller whose tenant context does not match must
            // fail rather than silently locking (and guarding) another account.
            var lockedAccount = await accounts.GetCurrentLockedAsync(token);
            if (lockedAccount is null || lockedAccount.Id != accountId)
                return Result.Failure(Error.NotFound("Accounts", accountId));

            var user = await db.Users.FirstOrDefaultAsync(
                u => u.Id == userId && u.AccountId == accountId, token);
            if (user is null)
                return Result.Failure(Error.NotFound("Users", userId));

            var actor = await RequireActiveOwnerAsync(accountId, actingUserId, token);
            if (actor.IsFailure)
                return actor;

            // TRUE NO-OP: already disabled. Skips the second epoch bump, the
            // restamped DisabledAt (which would rewrite the answer to "when
            // were they disabled") and the audit row.
            if (user.DisabledAt is not null)
                return Result.Success();

            var targetIsOwner = await (
                from userRole in db.UserRoles
                join r in db.Roles on userRole.RoleId equals r.Id
                where userRole.UserId == userId && r.Name == Cluckwork.Domain.Modules.Farm.Contracts.Roles.Owner
                select r.Name).AnyAsync(token);
            if (targetIsOwner
                && await CountOtherActiveOwnersAsync(accountId, userId, token) == 0)
            {
                // Same wording discipline as ConcurrencyConflict(): step-up is
                // unconditional here and its grant is already spent by the time
                // this guard runs, so the copy must not imply a bare resubmit.
                return Result.Failure(Error.Validation(
                    "Users.LastOwner",
                    "This is the account's last active Owner — promote or re-enable another Owner first, "
                    + "then confirm your password again."));
            }

            var now = timeProvider.GetUtcNow();
            user.DisabledAt = now;
            user.DisabledBy = actingUserId;

            // Rotate the target's SecurityStamp. CredentialEpoch kills bearer
            // and refresh tokens, but a step-up grant (#308) is a THIRD,
            // separate credential validated against SecurityStamp — without
            // this, a grant minted moments before the disable would still be
            // spendable once the user is re-enabled. Identity's
            // UserStore.UpdateAsync swallows a concurrency loss into a FAILED
            // IdentityResult rather than throwing, so the result is inspected
            // for the exact "ConcurrencyFailure" code separately from whatever
            // the SaveChangesAsync catch below handles.
            var stampRotated = await userManager.UpdateSecurityStampAsync(user);
            if (!stampRotated.Succeeded)
                return Result.Failure(IsConcurrencyFailure(stampRotated)
                    ? ConcurrencyConflict()
                    : Error.Validation("Users.DisableFailed", Describe(stampRotated)));

            user.CredentialEpoch++;
            await RevokeAllActiveForUserAsync(user.Id, now, token);

            await audit.WriteAsync(AuditActions.UserDisabled, "User", user.Id,
                reason: reason, details: null, ct: token);

            try
            {
                await db.SaveChangesAsync(token);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Result.Failure(ConcurrencyConflict());
            }
            await transaction.CommitAsync(token);
            return Result.Success();
        }, ct);

    // #356 — re-enable. Deliberately asymmetric with DisableUserAsync on the
    // CREDENTIAL side only: it clears the two Disabled* columns and audits;
    // no epoch bump, no restored pre-disable epoch, no token re-issue. That
    // asymmetry IS the feature — it is what stops a re-enable from
    // resurrecting every access token the disable killed. It DOES rotate
    // SecurityStamp/ConcurrencyStamp below (round-3/round-5 review of #492) —
    // required, not optional: without it a stale full-entity write from a
    // concurrent SetUserPassword could silently restore DisabledAt behind a
    // 204. Do not read "touches NOTHING else" as covering that rotation.
    public Task<Result> EnableUserAsync(
        Guid accountId, Guid userId, Guid actingUserId, CancellationToken ct = default) =>
        AmbientTransaction.RunAsync(db.Database, async (transaction, token) =>
        {
            // Same unconditional lock as the disable path. Enable removes no
            // Owner, so there is no survivor count to protect — the lock is
            // here so a disable and an enable of the same user serialize
            // instead of interleaving into an inconsistent DisabledAt/epoch
            // pair.
            var lockedAccount = await accounts.GetCurrentLockedAsync(token);
            if (lockedAccount is null || lockedAccount.Id != accountId)
                return Result.Failure(Error.NotFound("Accounts", accountId));

            var user = await db.Users.FirstOrDefaultAsync(
                u => u.Id == userId && u.AccountId == accountId, token);
            if (user is null)
                return Result.Failure(Error.NotFound("Users", userId));

            var actor = await RequireActiveOwnerAsync(accountId, actingUserId, token);
            if (actor.IsFailure)
                return actor;

            // TRUE NO-OP: already active. No audit row for a non-event.
            if (user.DisabledAt is null)
                return Result.Success();

            // Both columns describe ONE live fact. Leaving DisabledBy behind
            // would be a column that reads as current and is not; the history
            // lives in the audit trail, which both directions write.
            user.DisabledAt = null;
            user.DisabledBy = null;

            // Rotate the stamp — and note this does NOT breach the "enable
            // touches nothing else" asymmetry, which exists to stop a re-enable
            // REVIVING a credential. A rotation only ever invalidates.
            //
            // It is here because a plain SaveChangesAsync leaves ConcurrencyStamp
            // untouched, while Identity's UserStore.UpdateAsync issues a
            // FULL-ENTITY update guarded on that same stamp. So an Owner's
            // concurrent SetUserPassword — which reads the user tracked, then
            // spends the whole PBKDF2 window in GeneratePasswordResetTokenAsync
            // /ResetPasswordAsync before writing — would still match the
            // unrotated stamp and write DisabledAt back from its pre-enable
            // snapshot. The Owner would have received 204 and a User.Enabled
            // audit row for an enable that was silently undone, with the user
            // still locked out. Rotating makes that stale write lose its CAS.
            //
            // Pinned by Enable_RotatesBothStamps_SoAStaleFullEntityWriteCannotRevertIt,
            // NOT by ConcurrentStampChange_DuringTheEnableItself_Is409: in that
            // test the FENCE changes the stamp, which EF's own ConcurrencyStamp
            // token catches with or without this rotation, so it stays green
            // when the rotation is deleted. Only a direct assertion that the
            // stamp VALUE changed pins this line.
            var stampRotated = await userManager.UpdateSecurityStampAsync(user);
            if (!stampRotated.Succeeded)
                return Result.Failure(IsConcurrencyFailure(stampRotated)
                    ? ConcurrencyConflict()
                    : Error.Validation("Users.EnableFailed", Describe(stampRotated)));

            await audit.WriteAsync(AuditActions.UserEnabled, "User", user.Id,
                reason: null, details: null, ct: token);

            try
            {
                await db.SaveChangesAsync(token);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Result.Failure(ConcurrencyConflict());
            }
            await transaction.CommitAsync(token);
            return Result.Success();
        }, ct);
}
