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
    public static async Task<bool> RecordFailedAccessAsync(
        UserManager<ApplicationUser> userManager, AppDbContext db, ApplicationUser user)
    {
        // Bind this durable failure to the credential state whose password was
        // actually rejected. A concurrent disable or password reset supersedes
        // that proof; after a concurrency reload, never charge the stale guess
        // to the newer state (or leave a disabled account locked on re-enable).
        var attemptedCredentialEpoch = user.CredentialEpoch;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            // Merge note (#364's boundary meets #273's transition detection):
            // both apply, and the only thing to DECIDE is what the boundary
            // returns now that this method reports a bool. `false` is the
            // answer — it stops precisely because no failure was charged to
            // this credential, so there is no lockout TRANSITION to report.
            // Returning true would fire Auth.AccountLockedOut for an attempt
            // that was deliberately not recorded, against the very credential
            // the guard exists to protect.
            if (user.DisabledAt is not null || user.CredentialEpoch != attemptedCredentialEpoch)
                return false;

            // Snapshot taken fresh on EVERY attempt (including retries after a
            // reload) — see the P2d note above for why entry-time alone is not
            // enough.
            var wasLockedOut = await userManager.IsLockedOutAsync(user);
            var result = await SingleAttemptExecution.RunAsync(
                db.Database, () => userManager.AccessFailedAsync(user));
            if (result.Succeeded)
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

    // The counterpart, and it lives here for the same reason RecordFailedAccess
    // does: BOTH password oracles clear the counter after a correct password
    // (LoginAsync and StepUpGrantService.IssueAsync), so a second copy is a
    // second thing to forget to update.
    //
    // #269 review (#350, codex round 5 sweep) — clearing the counter is a
    // convenience, NOT a security control: leaving it set only means the next
    // wrong password counts from a higher base, i.e. it errs TOWARD the #128
    // lockout, never away from it. So it must never be able to fail a request
    // that has already proven the credential — and before this it could.
    //
    // Identity's UserStore.UpdateAsync swallows a concurrency loss into
    // IdentityResult.Failed rather than throwing, and both call sites discarded
    // that result. Two different things produce it, and BOTH leave `user` tracked
    // as Modified carrying original values the row no longer has:
    //
    //   * a genuine parallel writer, and
    //   * this save's own REPLAY. It sits outside any user-initiated transaction
    //     (login is anonymous, so IdempotencyMiddleware's tenant gate skips it;
    //     /auth/step-up is on ResponseNotCacheable), so under EnableRetryOnFailure
    //     the execution strategy replays it. On the ambiguous commit the replay
    //     re-issues the UPDATE under the now superseded ConcurrencyStamp and
    //     matches 0 rows — the identical misread RecordFailedAccessAsync
    //     documents above.
    //
    // LoginAsync's refresh-token INSERT shares this DbContext, so its SaveChanges
    // then re-flushed the poisoned entity and threw. Measured pre-fix: a CORRECT
    // password answered 409 "Concurrency conflict" and issued no token at all,
    // even though the reset itself had committed.
    //
    // ReloadAsync refreshes original AND current values from the row — the same
    // surgical move the reload loop above makes, and deliberately not a blanket
    // db.ChangeTracker.Clear(), which would drop a longer-lived caller's pending
    // writes on a shared context (see SingleAttemptExecution). Note the fix is to
    // READ the result, not to stop the retry: the retry is what absorbs the
    // ordinary blip, and the parallel-writer case it also covers was never a
    // retry problem at all.
    public static async Task ResetFailedAccessCountAsync(
        UserManager<ApplicationUser> userManager, AppDbContext db, ApplicationUser user,
        CancellationToken ct = default)
    {
        var reset = await userManager.ResetAccessFailedCountAsync(user);
        if (!reset.Succeeded)
            await db.Entry(user).ReloadAsync(ct);
    }
}
