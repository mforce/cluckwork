using Cluckwork.Application.Common;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

public sealed partial class IdentityProvider
{
    // #355 — promote/demote. AmbientTransaction shape mirrors
    // ResetPasswordAndRevokeAsync: joins IdempotencyMiddleware's ambient
    // request transaction when one is open; on the owned path (a non-HTTP
    // caller) this is NOT retried — a replay would re-run the role mutation
    // and re-append the audit row a second time (#269, same reasoning as the
    // sibling password-reset method).
    public Task<Result> ChangeUserRoleAsync(
        Guid accountId, Guid userId, string? role, Guid actingUserId, CancellationToken ct = default) =>
        AmbientTransaction.RunAsync(db.Database, async (transaction, token) =>
        {
            // The account-wide lock, taken UNCONDITIONALLY — even a change
            // that could never affect the Owner count (e.g. Manager ->
            // Sales) — so no future role-changing path can silently miss it
            // (matches UpdateFarmSettingsHandler's own unconditional-locking
            // precedent for #162). The result is VALIDATED, not discarded:
            // GetCurrentLockedAsync resolves from the ambient TenantContext,
            // not from the accountId parameter, so a caller whose tenant
            // context doesn't actually match accountId must fail rather than
            // silently locking (and guarding) the wrong account.
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

            var currentRoleNames = (await userManager.GetRolesAsync(user)).ToList();

            // TRUE NO-OP: the requested role set (Worker -> {}, otherwise
            // {role}) equals the target's ACTUAL current role-row set. This
            // single set-equality check correctly handles Worker->Worker
            // (both sides {}), an ordinary unchanged role ({X} == {X}), and
            // fails correctly for the multi-role adversarial case
            // ({Owner,Manager} != {Owner}) — a stray extra row is a real
            // state change even when the requested role already matches one
            // of the rows.
            var requestedRoleSet = role is null
                ? new HashSet<string>()
                : new HashSet<string> { role };
            if (requestedRoleSet.SetEquals(currentRoleNames))
                return Result.Success();

            // LAST-OWNER GUARD: only relevant on an actual demotion away
            // from Owner. Disabled Owners are excluded from the survivor
            // count — a no-op today (nothing sets DisabledAt yet), but
            // closes a real future landmine: once the sibling disable-user
            // slice ships, a farm with one active Owner and one already-
            // disabled Owner could otherwise have its only WORKING Owner
            // demoted, because a naive count still sees "2 Owners."
            if (currentRoleNames.Contains(Cluckwork.Domain.Modules.Farm.Contracts.Roles.Owner)
                && role != Cluckwork.Domain.Modules.Farm.Contracts.Roles.Owner)
            {
                if (await CountOtherActiveOwnersAsync(accountId, userId, token) == 0)
                    return Result.Failure(Error.Validation(
                        "Users.LastOwner",
                        "This is the account's last Owner — promote another user before demoting this one."));
            }

            // Apply: remove every current role row (PLURAL — Identity permits
            // more than one row per user even though every ordinary write
            // path here assigns exactly one; a singular removal of just one
            // role could silently leave a second stray row behind), then add
            // the new one if not Worker.
            //
            // Identity's UserStore.UpdateAsync swallows a concurrency loss
            // into a FAILED IdentityResult rather than throwing (this
            // codebase's own documented prior incident — see
            // AccountLockout.cs's comment on RecordFailedAccessAsync /
            // ResetFailedAccessCountAsync) — so each result is inspected for
            // the exact "ConcurrencyFailure" code, mapped to Users.Conflict,
            // separately from whatever the final SaveChangesAsync catch
            // below handles.
            if (currentRoleNames.Count > 0)
            {
                var removed = await userManager.RemoveFromRolesAsync(user, currentRoleNames);
                if (!removed.Succeeded)
                    return Result.Failure(IsConcurrencyFailure(removed)
                        ? Error.Conflict("Users.Conflict", "The user was modified by another request. Reload and retry.")
                        : Error.Validation("Users.RoleChangeFailed", Describe(removed)));
            }

            if (role is not null)
            {
                if (!Cluckwork.Domain.Modules.Farm.Contracts.Roles.Assignable.Contains(role))
                    return Result.Failure(Error.Validation(
                        "Users.UnknownRole", $"'{role}' is not an assignable role."));

                if (!await roleManager.RoleExistsAsync(role))
                {
                    var roleCreated = await roleManager.CreateAsync(
                        new ApplicationRole { Id = Guid.NewGuid(), Name = role });
                    if (!roleCreated.Succeeded)
                        return Result.Failure(Error.Validation("Users.RoleChangeFailed", Describe(roleCreated)));
                }

                var added = await userManager.AddToRoleAsync(user, role);
                if (!added.Succeeded)
                    return Result.Failure(IsConcurrencyFailure(added)
                        ? Error.Conflict("Users.Conflict", "The user was modified by another request. Reload and retry.")
                        : Error.Validation("Users.RoleChangeFailed", Describe(added)));
            }

            // Rotate the TARGET's SecurityStamp. CredentialEpoch kills bearer
            // and refresh tokens, but a step-up grant (#308) is a THIRD,
            // separate credential validated against SecurityStamp, not the
            // epoch (StepUpGrantService.ValidateAsync) — without this, a
            // grant issued to the target just before their own role changed
            // (e.g. right before being promoted to Owner) would still be
            // spendable after they sign back in with a fresh epoch (codex
            // review, PR #475). Same IdentityResult-concurrency handling as
            // the role-row mutations above, for the same reason.
            var stampRotated = await userManager.UpdateSecurityStampAsync(user);
            if (!stampRotated.Succeeded)
                return Result.Failure(IsConcurrencyFailure(stampRotated)
                    ? Error.Conflict("Users.Conflict", "The user was modified by another request. Reload and retry.")
                    : Error.Validation("Users.RoleChangeFailed", Describe(stampRotated)));

            user.CredentialEpoch++;
            await RevokeAllActiveForUserAsync(user.Id, timeProvider.GetUtcNow(), token);

            // "Worker" sentinel for an empty set, matching CreateUserAsync's
            // own audit convention (role ?? "Worker") — an array so the
            // multi-role cleanup-only case (a stray row removed, the
            // effective/highest role unchanged) reads as the real state
            // change it is (e.g. oldRoles: ["Admin","Manager"], newRoles:
            // ["Admin"]) instead of a misleading single "X -> X" string.
            var oldRoles = currentRoleNames.Count > 0 ? currentRoleNames.ToArray() : ["Worker"];
            var newRoles = role is not null ? new[] { role } : ["Worker"];
            await audit.WriteAsync(AuditActions.UserRoleChanged, "User", user.Id,
                reason: null, details: new { oldRoles, newRoles }, ct: token);

            try
            {
                await db.SaveChangesAsync(token);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Result.Failure(Error.Conflict(
                    "Users.Conflict", "The user was modified by another request. Reload and retry."));
            }
            await transaction.CommitAsync(token);
            return Result.Success();
        }, ct);
}
