using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

// #1274 decision 11: step-up's password step, shared with an enrolled user's
// change-password. It writes nothing but the lockout charge, and it never
// resets the counter: for an enrolled user only an accepted code does
// (decision 5).
internal static class CurrentPasswordCheck
{
    internal static readonly Error Incorrect =
        Error.Validation("Users.CurrentPasswordIncorrect", "Current password is incorrect.");

    // The user, tracked, when the password is right; null when refused.
    internal static async Task<ApplicationUser?> CheckCurrentPasswordAsync(
        UserManager<ApplicationUser> userManager, AppDbContext db, AuthSecurityEventLogger securityEvents,
        Guid accountId, Guid userId, string currentPassword)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null || user.AccountId != accountId)
        {
            // Always pay the PBKDF2 cost, exactly like LoginAsync, so a
            // missing/foreign user isn't measurably faster than a real check.
            userManager.PasswordHasher.VerifyHashedPassword(
                new ApplicationUser(), TimingEqualization.DummyHash, currentPassword);
            // #273 codex review (P1b) — this is a SECOND password oracle
            // (LoginAsync being the first); SecurityEvents.LoginFailed's own
            // doc already says it fires on /auth/login OR /auth/step-up (and
            // an enrolled user's /auth/change-password), on all three
            // unsuccessful branches, with the identical field set.
            securityEvents.LoginFailed();
            return null;
        }

        if (user.DisabledAt is not null)
        {
            // Match login's disabled-account path: pay exactly one real hash
            // cost for either password, but never let guesses mutate durable
            // failed-access/lockout state while the account is disabled.
            userManager.PasswordHasher.VerifyHashedPassword(
                user, user.PasswordHash ?? TimingEqualization.DummyHash, currentPassword);
            return null;
        }

        // #128 per-account lockout, same as LoginAsync. This endpoint is a SECOND
        // password oracle — and the one guarding Owner takeover — so leaving it
        // out would have left only the per-IP limiter here, which a distributed
        // attacker rotating source IPs walks straight around. A locked account is
        // refused with the same error and still pays PBKDF2, so the reply never
        // reveals that the account is locked rather than the password wrong.
        if (await userManager.IsLockedOutAsync(user))
        {
            userManager.PasswordHasher.VerifyHashedPassword(
                user, user.PasswordHash ?? TimingEqualization.DummyHash, currentPassword);
            // #273 codex review (P1b) — same as LoginAsync's already-locked
            // branch: re-fires LoginFailed, never AccountLockedOut again (that
            // fired once, at the transition).
            securityEvents.LoginFailed();
            return null;
        }

        if (!await userManager.CheckPasswordAsync(user, currentPassword))
        {
            // #273 codex review (P1b) — the bool this returns was previously
            // discarded, so a failed step-up attempt never fired LoginFailed
            // and a threshold-crossing one never fired AccountLockedOut: a
            // failed /auth/step-up — a privileged password oracle — was
            // invisible to security telemetry. Same shape as LoginAsync's
            // wrong-password branch.
            //
            // Emitted BEFORE persisting lockout state (codex review round 2):
            // RecordFailedAccessAsync is a durable write that can throw, and a
            // throw there must not silently drop LoginFailed — the password was
            // already confirmed wrong.
            securityEvents.LoginFailed();
            if (await AccountLockout.RecordFailedAccessAsync(userManager, db, user))
                securityEvents.AccountLockedOut(user.Id);
            return null;
        }

        return user;
    }

    // An enrolled user's proof, for VerifyAsync. A failed rehash save inside
    // CheckPasswordAsync leaves the user Modified, which the user-row lock
    // refuses, so it is reloaded. The epoch is the one the password was
    // checked against: a password change that landed in between moves it, and
    // VerifyAsync then answers Superseded.
    internal static async Task<ProofBinding> BindForSecondFactorAsync(
        AppDbContext db, ApplicationUser user, CancellationToken ct)
    {
        var checkedEpoch = user.CredentialEpoch;
        if (db.Entry(user).State == EntityState.Modified)
            await db.Entry(user).ReloadAsync(ct);
        return new ProofBinding(checkedEpoch, ProofBinding.HashStamp(user.SecurityStamp), SignIns: null);
    }
}
