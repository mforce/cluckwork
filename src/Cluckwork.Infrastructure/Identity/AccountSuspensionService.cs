namespace Cluckwork.Infrastructure.Identity;

using Cluckwork.Application.Features.Accounts;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

// #532 — takes a farm offline and brings it back. No CLI or HTTP surface here:
// #534 ships the operator verbs and calls this. Shipping it callerless matches
// #531, which added Account.Suspend()/Reactivate() the same way.
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
// ConcurrencyStamp is rotated in the SAME statement, and that is a fence, not
// housekeeping: Identity's UserStore.UpdateAsync issues a FULL-ENTITY update
// guarded on that stamp, so a concurrent same-user Identity write that read the
// row BEFORE this transaction would otherwise still match and write its STALE
// CredentialEpoch back — silently un-suspending that user. Same mechanism, same
// reason, as PersistentStepUpGrantRegistry.RecordLogoutAsync.
//
// The guarantee above is IMMEDIATE for USE, not for ISSUANCE, and this is said
// plainly rather than over-claimed: login checks Account.IsActive (AuthEndpoints
// :186) and mints (:213) in two separate steps. A suspension that commits in
// that window leaves a refresh-token row that POST-DATES the revocation sweep,
// and login returns 200. The minted credential is inert, though: the access
// token is refused on its next request (CredentialEpochMiddleware re-reads
// IsActive live, with the epoch already bumped), the refresh token is refused
// by RefreshAsync's own suspended-farm check, and reactivation's epoch bump and
// revocation then destroy the row for good. The test that pins that destruction
// is ReactivationRevokesTheSessionsMintedBetweenSuspendAndReactivate, which
// inserts exactly this artifact on purpose because the race is not reproducible
// on demand. Closing the window itself would require login to take a FOR SHARE
// lock on the account row inside its issuance transaction — a lock on the login
// hot path, and it creates the account-then-token lock ordering that could
// deadlock against this service (this service takes account first, then the
// user and token rows, and every other locking path does the same). That is
// deliberately not done.
public sealed class AccountSuspensionService(
    AppDbContext db, TenantContext tenant, IAccountRepository accounts, TimeProvider timeProvider)
{
    public Task<Result> SuspendAsync(Guid accountId, CancellationToken ct = default) =>
        MutateAsync(accountId, account => account.Suspend(), _ => true, ct);

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
    public Task<Result> ReactivateAsync(Guid accountId, CancellationToken ct = default) =>
        MutateAsync(accountId, account => account.Reactivate(), account => !account.IsActive, ct);

    private Task<Result> MutateAsync(
        Guid accountId, Action<Domain.Accounts.Account> mutate,
        Func<Domain.Accounts.Account, bool> shouldRevokeSessions, CancellationToken ct)
    {
        // The repository's locked read resolves the row from the AMBIENT tenant,
        // not from a parameter, so resolving first is a precondition and not a
        // formality — without it the lock targets Guid.Empty, matches no row, and
        // this returns NotFound for every real farm. Same order AdminRecoveryService
        // uses. TenantContext is single-assignment (#546), so one account per scope.
        tenant.Resolve(accountId);

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
                return Result.Failure(Error.NotFound("Accounts", accountId));

            // Evaluated against the PRE-mutation state, so Reactivate can ask
            // "was this farm suspended?" rather than "is it active now?", which
            // mutate() has already made true.
            var revokeSessions = shouldRevokeSessions(account);

            // Through the aggregate, never a bare IsActive assignment: Suspend()
            // and Reactivate() bump Version, which is the EF concurrency token
            // (AGENTS.md — every aggregate mutation must bump it).
            mutate(account);

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

            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return Result.Success();
        }, ct);
    }
}
