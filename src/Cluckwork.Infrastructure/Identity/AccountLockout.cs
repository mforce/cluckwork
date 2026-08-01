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
    public static async Task RecordFailedAccessAsync(
        UserManager<ApplicationUser> userManager, AppDbContext db, ApplicationUser user)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if ((await userManager.AccessFailedAsync(user)).Succeeded)
                return;
            // The write lost the concurrency race. FindById would hand back the
            // same identity-map instance (stale stamp), so refresh the tracked
            // entity's values from the DB before retrying — `db` is the same
            // scoped context the UserManager store writes through.
            await db.Entry(user).ReloadAsync();
        }
    }
}
