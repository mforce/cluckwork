namespace Cluckwork.Infrastructure.Identity;

using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

// #128 per-account lockout, shared by EVERY password-verification oracle rather
// than living inside one of them.
//
// Login was not the only place a caller can present a password: #308 added
// /auth/step-up, which re-confirms the current password to authorise an Owner
// takeover. Guarding only login leaves the per-IP rate limiter (#143) as the
// sole defence on the other oracle — and per-account lockout exists precisely
// because a distributed attacker rotating source IPs walks around a per-IP
// limit. The two controls are meant to work together; splitting them silently
// weakens whichever surface forgets one.
//
// Keep this shared: a second copy is a second thing to forget to update.
internal static class AccountLockout
{
    // AccessFailedAsync persists the increment under the user row's optimistic
    // concurrency stamp. Parallel failed attempts for one account would otherwise
    // drop the losing writer's increment, letting a distributed burst dodge the
    // threshold — the exact attack this exists to stop. Retry against a freshly
    // reloaded user until it commits.
    //
    // Bounded generously: only the concurrency-conflict path retries, and the
    // per-account contention that produces conflicts is itself capped by the
    // per-IP rate limiter. The cap prevents an unbounded loop while still letting
    // every real failure land under normal contention.
    //
    // #273 — returns whether THIS call is the one that crossed the lockout
    // threshold (the account was NOT locked immediately before its successful
    // write, and IS immediately after). Callers use that to fire the
    // Auth.AccountLockedOut security event exactly once per lockout episode,
    // not on every subsequent failed attempt against an already-locked
    // account.
    //
    // #273 codex review (P2d) — "unlocked on entry" (every caller here already
    // checks IsLockedOutAsync before calling this method) is true only for the
    // FIRST attempt, not for one reached after a reload. Two concurrent
    // failures one attempt below the threshold can both pass that precheck.
    // The winner's AccessFailedAsync commits and locks the account; the
    // loser's write then loses the concurrency race, reloads, and — on retry —
    // sees the ALREADY-locked row the winner just committed. That retry's own
    // AccessFailedAsync call typically succeeds (it's just an ordinary
    // increment on the now-current row), so reporting "succeeded ->
    // IsLockedOutAsync" unconditionally made the LOSER report a transition it
    // never caused too, double-firing AccountLockedOut for one lockout
    // episode. Snapshotting "was this row already locked" immediately before
    // each attempt's write (not just once at method entry) is what lets a
    // losing writer recognize a lockout it merely observed on reload, rather
    // than caused.
    public static async Task<bool> RecordFailedAccessAsync(
        UserManager<ApplicationUser> userManager, AppDbContext db, ApplicationUser user)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            // Snapshot taken fresh on EVERY attempt (including retries after a
            // reload) — see the P2d note above for why entry-time alone is not
            // enough.
            var wasLockedOut = await userManager.IsLockedOutAsync(user);
            if ((await userManager.AccessFailedAsync(user)).Succeeded)
                return !wasLockedOut && await userManager.IsLockedOutAsync(user);
            // The write lost the concurrency race. FindById would hand back the
            // same identity-map instance (stale stamp), so refresh the tracked
            // entity's values from the DB before retrying — `db` is the same
            // scoped context the UserManager store writes through.
            await db.Entry(user).ReloadAsync();
        }
        // Exhausted retries without a successful write — the caller's failed
        // attempt was never actually recorded, so this can't be reported as a
        // lockout transition.
        return false;
    }
}
