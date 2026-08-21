namespace Cluckwork.Infrastructure.Identity;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Accounts;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

// #532 — takes a farm offline and brings it back. #534 ships the operator verbs
// (`suspend-account` / `reactivate-account`) that call this. Shipping it
// callerless matched #531, which added Account.Suspend()/Reactivate() the same
// way.
//
// The whole point is that suspension is IMMEDIATE. Three things make it so, and
// all three must happen in ONE transaction:
//
//   1. Account.IsActive goes false, which CredentialEpochMiddleware reads on
//      every authenticated request and login/refresh read on their own paths.
//   2. Every user's CredentialEpoch is bumped, which kills their outstanding
//      ACCESS tokens (the middleware compares it) and their refresh tokens
//      (RefreshAsync compares stored.IssuedEpoch against it).
//   3. Every user's SecurityStamp is rotated, which kills outstanding STEP-UP
//      grants — those bind to the stamp, never to CredentialEpoch, so without
//      this an Owner's pre-suspension grant survives a suspend/reactivate cycle
//      and can still be spent on a privileged operation.
//
// #534 adds a fourth thing to that same transaction: the audit row. IAuditWriter
// appends to the CALLER's unit of work and never saves (see its header), so the
// row commits with the suspension or not at all — writing it from the CLI verb
// after this returned would leave a window where a farm is offline with no trail.
//
// ConcurrencyStamp is rotated in the SAME statement, and that is a fence, not
// housekeeping: Identity's UserStore.UpdateAsync issues a FULL-ENTITY update
// guarded on that stamp, so a concurrent same-user Identity write that read the
// row BEFORE this transaction would otherwise still match and write its STALE
// CredentialEpoch back — silently un-suspending that user. Same mechanism, same
// reason, as PersistentStepUpGrantRegistry.RecordLogoutAsync.
//
// The guarantee above is IMMEDIATE for USE, not for ISSUANCE, and this is said
// plainly rather than over-claimed: login checks Account.IsActive (the
// `if (!account.IsActive)` branch in AuthEndpoints) and mints inside
// IdentityProvider.LoginAsync, in two separate steps. A suspension that commits in
// that window leaves a refresh-token row that POST-DATES the revocation sweep,
// and login returns 200. The minted credential is inert, though: the access
// token is refused on its next request (CredentialEpochMiddleware re-reads
// IsActive live, with the epoch already bumped), the refresh token is refused
// by RefreshAsync's own suspended-farm check, and reactivation's epoch bump and
// revocation then destroy the row for good. The test that pins that destruction
// is ReactivationRevokesTheSessionsMintedBetweenSuspendAndReactivate, which
// inserts exactly this artifact on purpose because the race is not reproducible
// on demand. Closing the window itself would require login to take a FOR SHARE
// lock on the account row inside its issuance transaction. That lock is NOT
// taken, for cost, not for deadlock: it puts a lock plus an extra round trip on
// the login hot path in order to close a window whose only product is a
// credential that can never be used. (Consistent account-first lock ordering
// would only SERIALISE the two paths — it could not deadlock them, since every
// locking path takes the account row first.)
public sealed class AccountSuspensionService(
    AppDbContext db, TenantContext tenant, IAccountRepository accounts, TimeProvider timeProvider,
    IAuditWriter audit, CurrentUserContext currentUser)
{
    // #534 — Changed answers the PRE-mutation question "did this command
    // actually transition the farm?". That is what lets the verb tell an
    // operator their re-run was a no-op, and it is what gates the audit row. It
    // is deliberately NOT the same question as "were sessions revoked": a
    // suspend re-run revokes again and still reports Changed = false.
    public Task<Result<AccountLifecycleOutcome>> SuspendAsync(
        Guid accountId, string? reason, CancellationToken ct = default) =>
        MutateAsync(accountId, suspending: true, reason, ct);

    // Reactivation ALSO revokes, and that is not symmetry for its own sake: a
    // session minted in the instant before the suspension committed would
    // otherwise become usable again the moment the farm comes back. Revoking on
    // the way in and on the way out means nothing survives the cycle.
    //
    // #532 round 3 — but ONLY when the farm was actually suspended. Reactivate()
    // is unconditional by design (Account.cs), and #534's reactivate verb is the
    // kind of command an operator retries. Revoking unconditionally would sign
    // out every member of staff on an already-active farm, mid-shift, and kill
    // their step-up grants, for a command that changed nothing.
    public Task<Result<AccountLifecycleOutcome>> ReactivateAsync(
        Guid accountId, string? reason, CancellationToken ct = default) =>
        MutateAsync(accountId, suspending: false, reason, ct);

    private Task<Result<AccountLifecycleOutcome>> MutateAsync(
        Guid accountId, bool suspending, string? reason, CancellationToken ct)
    {
        // The repository's locked read resolves the row from the AMBIENT tenant,
        // not from a parameter, so resolving first is a precondition and not a
        // formality — without it the lock targets Guid.Empty, matches no row, and
        // this returns NotFound for every real farm. Same order AdminRecoveryService
        // uses. TenantContext is single-assignment (#546), so one account per scope.
        tenant.Resolve(accountId);

        // #500 — IAuditWriter fails closed on an unresolved ACTOR as well as an
        // unresolved tenant. These verbs have no signed-in human by design (an
        // operator at a shell), so they declare WHICH non-person they are,
        // exactly as AdminRecoveryService does for break-glass. Resolved here
        // rather than by the CLI verb because epic #530 decision 2 rejected a
        // cross-tenant operator HTTP API outright — there is no future
        // human-actor caller whose identity this would override.
        currentUser.ResolveSystemActor(
            suspending ? SystemActors.SuspendAccount : SystemActors.ReactivateAccount);

        return AmbientTransaction.RunAsync(db.Database, async (transaction, token) =>
        {
            // Account first, then the user and token rows. Every other path that
            // locks an account does the same, and inverting it is the only way to
            // deadlock against them.
            // No account.Id != accountId clause here (it IS live in
            // IdentityProvider.ChangeUserRoleAsync, where the tenant comes from
            // middleware): the repository read is already tenant-keyed —
            // tenant.Resolve(accountId) above set the ambient tenant, and
            // GetCurrentLockedAsync selects WHERE "Id" = {tenant.AccountId} —
            // so a mismatch is unreachable.
            var account = await accounts.GetCurrentLockedAsync(token);
            if (account is null)
                return Result.Failure<AccountLifecycleOutcome>(Error.NotFound("Accounts", accountId));

            // Both read against the PRE-mutation state, so reactivate can ask
            // "was this farm suspended?" rather than "is it active now?", which
            // the mutation below has already made true.
            //
            // They are two separate questions and are kept apart on purpose:
            // they coincide for reactivate and DIVERGE for suspend, where a
            // re-run revokes (revokeSessions true) without transitioning
            // anything (stateChanged false). Folding them into one flag makes a
            // suspend re-run either skip its revoke or append a duplicate audit
            // row, depending on which way it is folded.
            var stateChanged = suspending ? account.IsActive : !account.IsActive;
            // Suspend revokes UNCONDITIONALLY (#534, owner decision): an operator
            // re-running `suspend-account` is the only reachable way to mop up a
            // credential minted inside the login/suspend race window described in
            // the header, and re-revoking on an offline farm signs out nobody who
            // could be working. Reactivate revokes only on a real transition —
            // see its comment above.
            var revokeSessions = suspending || stateChanged;

            // Through the aggregate, never a bare IsActive assignment: Suspend()
            // and Reactivate() bump Version, which is the EF concurrency token
            // (AGENTS.md — every aggregate mutation must bump it).
            //
            // Gated on stateChanged (#534 review round 1), and the gate is not
            // cosmetic. Both aggregate methods bump Version UNCONDITIONALLY by
            // design (#531, pinned by AccountSlugTests), and Version is the token
            // UpdateFarmSettingsHandler compares a Farm Settings save against. An
            // ungated no-op re-run therefore advances it for a command that
            // reported it changed nothing, and the next save from anyone holding
            // that form open fails with Account.VersionMismatch. Gating HERE
            // rather than in the aggregate keeps "every mutation bumps Version"
            // true: on a no-op there is now no mutation.
            //
            // The revoke sweep above is deliberately NOT gated by this: a suspend
            // re-run still re-revokes, which is the owner decision this slice
            // shipped, and it touches user and token rows, never the account row.
            if (stateChanged)
            {
                if (suspending)
                    account.Suspend();
                else
                    account.Reactivate();
            }

            if (revokeSessions)
            {
                var rotatedConcurrencyStamp = Guid.NewGuid().ToString();
                var rotatedSecurityStamp = Guid.NewGuid().ToString();

                // ONE statement, not a tracked loop. A tracked loop puts each
                // row's loaded ConcurrencyStamp in the WHERE, so any concurrent
                // Identity write on any user in the farm makes the whole
                // suspension throw DbUpdateConcurrencyException and roll back.
                await db.Users
                    .Where(user => user.AccountId == accountId)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(user => user.CredentialEpoch, user => user.CredentialEpoch + 1)
                            .SetProperty(user => user.ConcurrencyStamp, rotatedConcurrencyStamp)
                            .SetProperty(user => user.SecurityStamp, rotatedSecurityStamp),
                        token);

                // Refresh families die durably, so reactivation cannot resurrect
                // a pre-suspension session even though IsActive goes true again.
                await db.RefreshTokens
                    .Where(refreshToken => refreshToken.AccountId == accountId
                        && refreshToken.RevokedAt == null)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(
                            refreshToken => refreshToken.RevokedAt, timeProvider.GetUtcNow()),
                        token);
            }

            // Inside the transaction and before SaveChanges, so the row lands
            // with the suspension or not at all. Written only on a real
            // transition: a re-run re-revokes but appends nothing, which keeps
            // the trail one row per transition rather than one per keystroke.
            //
            // The action is a ternary of two AuditActions constants rather than a
            // forwarded parameter because AuditVocabularyCoverageTests fails
            // closed on any other shape at a WriteAsync call site (#258).
            if (stateChanged)
                await audit.WriteAsync(
                    suspending ? AuditActions.AccountSuspend : AuditActions.AccountReactivate,
                    nameof(Domain.Accounts.Account), accountId,
                    reason: reason,
                    // The same accountability payload break-glass records, and
                    // for the same reason: the actor names the COMMAND, not a
                    // person, so the shell it ran on is the only trace of who
                    // that was.
                    details: new { host = Environment.MachineName, osUser = Environment.UserName },
                    ct: token);

            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return Result.Success(new AccountLifecycleOutcome(stateChanged));
        }, ct);
    }
}

// Changed = "this command transitioned the farm", so a verb can report a no-op
// re-run without going back to the database to work out what it did.
public sealed record AccountLifecycleOutcome(bool Changed);
