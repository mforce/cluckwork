using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Application.Common;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

public sealed partial class IdentityProvider
{
    public async Task<Result> SetUserPasswordAsync(
        Guid accountId, Guid userId, string newPassword, CancellationToken ct = default)
    {
        // Account-scoped, exactly like UpdateUserAsync: a foreign user id doesn't
        // match and falls through to NotFound rather than resetting someone else's
        // tenant's credentials.
        var user = await db.Users.FirstOrDefaultAsync(
            u => u.Id == userId && u.AccountId == accountId, ct);
        if (user is null)
            return Result.Failure(Error.NotFound("Users", userId));

        return await ResetPasswordAndRevokeAsync(
            user, newPassword, AuditActions.UserPasswordSet, reason: null, details: null, ct);
    }

    // #356 (codex review of #492 round 2) — the reset paths need their own
    // copy: #360 made EVERY interactive SetUserPassword spend a single-use
    // step-up grant BEFORE the account lock is taken, so a losing interactive
    // reset is back to needing a fresh confirmation; offline `recover-admin`
    // has no password-confirmation grant at all. ConcurrencyConflict's wording
    // in IdentityProvider.DisableEnable.cs ("your
    // password confirmation has already been used") is true for interactive
    // resets but would still instruct the offline flow to redo something it
    // never did, so the copy hedges. Same Users.Conflict code — only the
    // human-facing sentence differs.
    private static Error ResetConcurrencyConflict() => Error.Conflict(
        "Users.Conflict",
        "The user was modified by another request. Reload and retry; "
        + "you may be asked to confirm your password again.");

    public async Task<Result> BreakGlassResetAsync(
        Guid accountId, Guid userId, string newPassword, string? reason,
        CancellationToken ct = default)
    {
        // Same account-scoped lookup as SetUserPasswordAsync — a foreign id
        // resolves to NotFound, never a cross-tenant reset.
        var user = await db.Users.FirstOrDefaultAsync(
            u => u.Id == userId && u.AccountId == accountId, ct);
        if (user is null)
            return Result.Failure(Error.NotFound("Users", userId));

        // Record WHERE the offline command ran (#265 review): the CLI has no
        // authenticated human, so host + OS user is what gives the break-glass
        // row real accountability beyond the free-text reason.
        //
        // #500 corrected the second half of this comment. The row is no longer
        // attributed to "(unresolved)" — AdminRecoveryService declares
        // SystemActors.BreakGlass before calling in, and IAuditWriter now
        // refuses an event with no actor at all. These details still matter for
        // exactly the reason above: the actor names the COMMAND, not a person.
        var details = new { host = Environment.MachineName, osUser = Environment.UserName };

        // A DISTINCT audit action + the operator's reason so a break-glass reset
        // stands out from an ordinary Owner-initiated one (#265).
        return await ResetPasswordAndRevokeAsync(
            user, newPassword, AuditActions.UserBreakGlassReset, reason, details, ct);
    }

    // Shared core (#165 SetUserPassword + #265 break-glass): reset the password
    // without the current one — which applies the full policy and rotates the
    // SecurityStamp — clear any lockout, evict every live session, and append one
    // audit row, all in a single transaction so the password change and the
    // session revocation land together or not at all (#165 review). Since #364
    // it also bumps CredentialEpoch in that same transaction (below), so an
    // already-issued access token is rejected by CredentialEpochMiddleware on
    // its very next request — no longer merely bounded by the ~15-min
    // access-token lifetime.
    private Task<Result> ResetPasswordAndRevokeAsync(
        ApplicationUser user, string newPassword, string auditAction, string? reason,
        object? details, CancellationToken ct)
    {
        // #307 — joins IdempotencyMiddleware's ambient request transaction
        // when one is open, instead of nesting a second one.
        //
        // #269 — the delegate shape is EnableRetryOnFailure's requirement (a
        // user-initiated transaction must be opened inside an execution
        // strategy), not a retry: AmbientTransaction's owned path — the one
        // `recover-admin` takes, having no ambient transaction — runs this
        // exactly once. Replaying it would flush the failed attempt's still-
        // Added audit row and refresh token a second time (EF does not detach
        // them), leaving a duplicate audit entry and an active token that was
        // never issued to anyone (#269 review).
        return AmbientTransaction.RunAsync(db.Database, async (transaction, token) =>
        {
            // Reset via a generated token — Identity's supported way to set a password
            // without the current one.
            var resetToken = await userManager.GeneratePasswordResetTokenAsync(user);
            var reset = await userManager.ResetPasswordAsync(user, resetToken, newPassword);
            if (!reset.Succeeded)
                // #356 — a lost CAS is separated from a rejected password. This
                // became reachable when EnableUserAsync started rotating the
                // stamp: that rotation is what makes a concurrent reset lose,
                // and Identity reports the loss as a FAILED IdentityResult, not
                // a throw. Without this split the loser is told "the new
                // password was rejected" — a 422 whose only actionable reading
                // is "choose a stronger password", for a password that was
                // never the problem. Reset-flavoured copy: #360 makes every
                // interactive reset spend a single-use step-up grant before the
                // lock, so an interactive loser is back to needing a fresh
                // confirmation; the offline recover-admin verb never had one.
                // The hedged sentence covers both.
                return Result.Failure(IsConcurrencyFailure(reset)
                    ? ResetConcurrencyConflict()
                    : Error.Validation("Users.PasswordRejected", Describe(reset)));

            // #283 — any successful password reset clears a pending first-run gate.
            // Covers an Owner using SetUserPassword or an offline break-glass reset
            // on a user who never got around to their forced first-run change; the
            // user obviously already has a working password at that point, so
            // there is nothing left to force. `user` is the same DbContext-tracked
            // instance db.SaveChangesAsync persists below — no separate save needed.
            user.MustChangePassword = false;
            user.CredentialEpoch++;

            // Clear any active lockout / failed-attempt count (#265 review). Without
            // this, the exact case break-glass exists for — a user locked out by
            // repeated failed logins — would get a fresh password that LoginAsync
            // still refuses until the lockout window expires (it checks
            // IsLockedOutAsync before the password), defeating the recovery.
            await userManager.ResetAccessFailedCountAsync(user);
            await userManager.SetLockoutEndDateAsync(user, null);

            await RevokeAllActiveForUserAsync(user.Id, timeProvider.GetUtcNow(), token);
            await audit.WriteAsync(auditAction, "User", user.Id,
                reason: reason, details: details, ct: token);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return Result.Success();
        }, ct);
    }

    public async Task<Result<TokenPair>> ChangeOwnPasswordAsync(
        Guid userId, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
            return Result.Failure<TokenPair>(Error.Validation(
                "Identity.InvalidCredentials", "Invalid email or password."));

        // One transaction around change + revoke + re-issue: the bulk revoke is
        // immediate SQL, so a failure before the fresh token is saved would sign
        // the caller out of everything while reporting an error (#165 review).
        // #307 — joins IdempotencyMiddleware's ambient request transaction
        // when one is open, instead of nesting a second one.
        //
        // #269 — this endpoint is EXEMPT from idempotency wrapping
        // (IdempotencyMiddleware.ResponseNotCacheable — a password change is
        // self-invalidating, so replay was never useful here), so every real
        // call takes AmbientTransaction's "owned" path. That path runs this
        // exactly once; the delegate shape is EnableRetryOnFailure's
        // requirement, not a retry. A replay here would both re-add the
        // refresh token the failed attempt already tracked as Added and
        // re-verify `currentPassword` against a hash a prior attempt may
        // already have changed (see SingleAttemptExecution).
        return await AmbientTransaction.RunAsync(db.Database, async (transaction, token) =>
        {
            // ChangePasswordAsync verifies the current password AND applies the policy.
            var changed = await userManager.ChangePasswordAsync(user, currentPassword, newPassword);
            if (!changed.Succeeded)
            {
                // Distinguish "your current password is wrong" from "the new one is
                // too weak" — the user needs to know which to fix. Neither leaks
                // anything: the caller is already authenticated as this user.
                var wrongCurrent = changed.Errors.Any(e => e.Code == "PasswordMismatch");
                if (wrongCurrent)
                    return Result.Failure<TokenPair>(Error.Validation(
                        "Users.CurrentPasswordIncorrect", "Current password is incorrect."));
                // A lost CAS here is a benign concurrent-writer race, not a bad
                // password: since #338 a logout rotates this row's ConcurrencyStamp
                // (and an admin reset/disable can too), so a change that read the
                // user before that commit loses its optimistic-concurrency check
                // and UserManager returns ConcurrencyFailure. Map it to the same
                // Users.Conflict every sibling write path uses, rather than telling
                // the user their new password was rejected.
                if (IsConcurrencyFailure(changed))
                    return Result.Failure<TokenPair>(ResetConcurrencyConflict());
                return Result.Failure<TokenPair>(Error.Validation("Users.PasswordRejected", Describe(changed)));
            }

            // #283 — this is the SPA's first-login "set your password" screen's
            // actual mechanism (it reuses this endpoint: the operator already
            // knows the generated first-run password as their "current" one).
            // Clearing the flag here is what lets the fresh token pair below omit
            // the must_change_password claim, un-gating the rest of the app.
            if (user.MustChangePassword)
                user.MustChangePassword = false;
            user.CredentialEpoch++;

            // Every session dies (other devices are signed out), then this caller gets
            // a fresh pair so the device that made the change stays signed in.
            var now = timeProvider.GetUtcNow();
            await RevokeAllActiveForUserAsync(user.Id, now, token);

            var (rawToken, tokenHash) = GenerateRefreshToken();
            db.RefreshTokens.Add(NewToken(user, tokenHash));
            await audit.WriteAsync(AuditActions.UserPasswordChanged, "User", user.Id,
                reason: null, details: null, ct: token);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);

            var roles = await userManager.GetRolesAsync(user);
            return Result.Success(jwtTokenService.CreateTokenPair(user, [.. roles], rawToken));
        }, ct);
    }
}
