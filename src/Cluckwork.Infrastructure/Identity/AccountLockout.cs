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
    // #269 review (#350, codex round 4) — each save runs through
    // SingleAttemptExecution, and that is what makes "Succeeded == false" MEAN
    // a parallel writer. Neither password oracle is inside a user-initiated
    // transaction (login is anonymous, so IdempotencyMiddleware's tenant gate
    // skips it; /auth/step-up is on its ResponseNotCacheable list), so under
    // EnableRetryOnFailure this save is a self-contained unit the execution
    // strategy will REPLAY. On the ambiguous commit — Postgres committed the
    // increment, the acknowledgment was lost — the replay re-issues the UPDATE
    // with the stale ConcurrencyStamp, matches 0 rows, and is reported as a
    // concurrency failure. The loop below cannot tell that apart from a real
    // conflict, so it would reload the ALREADY-incremented user and increment
    // it AGAIN: one wrong password costing two failed accesses, locking the
    // account at roughly half the configured threshold. (The same "one wrong
    // password, two increments" consequence shipped once already on a
    // different path in #336 — treat premature lockout as an availability
    // defect, not a cosmetic one.)
    //
    // Only the SAVE is wrapped. The reload is an ordinary read, replayable and
    // side-effect-free, so it keeps its automatic retry. A transient failure
    // inside the save now surfaces rather than being absorbed — deliberate,
    // and the pre-#269 behaviour; the remedy is the client trying again.
    public static async Task RecordFailedAccessAsync(
        UserManager<ApplicationUser> userManager, AppDbContext db, ApplicationUser user)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var result = await SingleAttemptExecution.RunAsync(
                db.Database, () => userManager.AccessFailedAsync(user));
            if (result.Succeeded)
                return;
            // The write lost the concurrency race. FindById would hand back the
            // same identity-map instance (stale stamp), so refresh the tracked
            // entity's values from the DB before retrying — `db` is the same
            // scoped context the UserManager store writes through.
            await db.Entry(user).ReloadAsync();
        }
    }
}
