namespace Cluckwork.Infrastructure.Identity;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

// #283 Part 2 — first-run admin provisioning. Invoked ONLY by the
// `bootstrap-admin` CLI command (Cli/BootstrapAdminCliCommand.cs), a one-shot
// operator command, never an HTTP endpoint and never a serving-boot side
// effect (mirrors AdminRecoveryService's #265 shape: a thin CLI wrapper
// handles args/stdout/exit-codes, this does the real work).
//
// The default account/Admin role/default egg grades are #283 Part 1 static
// reference data baked into the EF migrations via raw migrationBuilder.Sql
// with WHERE NOT EXISTS guards — always present once the schema is current.
// NO user row is ever baked into a migration (that would ship every
// deployment the same publicly-known credential): the first Owner is created
// HERE, on first run, with a freshly generated password nothing but this one
// process ever sees.
//
// Idempotent (#283 requirement): a re-run against an already-provisioned
// account (an Owner already exists) is a clean no-op success — never a
// duplicate Owner, never a second printed secret.
//
// PR #339 review — check-then-act race: two `bootstrap-admin` invocations
// starting at once can both observe "no Owner yet" before either commits,
// each mint a distinct Owner with its own generated password, and silently
// break the "exactly one first-run admin" premise. The whole
// check-and-create critical section below runs under a Postgres
// SESSION-scoped advisory lock (pg_advisory_lock/_unlock, not
// pg_advisory_xact_lock): the critical section is READS first and only then a
// transactional create, and a transaction-scoped lock cannot start before the
// transaction does — so it would leave the reads it exists to protect
// unguarded. A session lock, held for as long as this method keeps the
// connection explicitly open (OpenConnectionAsync's ref-count), spans the
// reads and the create alike. (Layer 3 below now opens that create transaction
// here rather than inside CreateUserAsync, which JOINS it; that does not
// change this reasoning — the reads still precede any transaction.) A concurrent second invocation blocks on
// the lock until the first commits, then observes the just-created Owner and
// takes the idempotent AlreadyProvisioned() branch — never a duplicate.
//
// PR #350 review round 2 (codex 3696740950) — that guarantee only holds while
// the lock is actually on the connection doing the work, and #269's
// EnableRetryOnFailure quietly broke it: the Owner/conflict READS are ordinary
// EF units of work, so a transient failure made the execution strategy retry
// them, and a retry RECONNECTS. The session-scoped lock lives on the physical
// connection, so the reconnect released it; the method then walked into
// CreateUserAsync holding nothing, and two invocations with different emails
// could each observe "no Owner" and each mint one. Verified against a real
// Postgres: pg_terminate_backend() on the pinned session right after the lock
// was taken, and provisioning still completed — an Owner created while
// pg_locks showed the lock held by nobody. Three layers close it, and all
// fail CLOSED (a failed bootstrap-admin is re-runnable and idempotent; a
// second farm Owner is not undoable):
//
//   1. The whole lock -> check -> create region runs as ONE non-replayed
//      attempt (SingleAttemptExecution). Inside an execution strategy every
//      nested EF operation is suspended, so no read can be retried onto a
//      fresh connection behind our back — a transient failure mid-region
//      surfaces as an error instead. Chosen over "reacquire the lock and
//      re-run the checks after a reconnect" because that is an implicit loop
//      whose correctness is hard to prove, and it would have to interleave
//      with CreateUserAsync's own transaction; this is a straight line.
//   2. Before the create, lock ownership is PROVEN rather than assumed:
//      pg_locks is asked whether THIS backend still holds it. That covers the
//      residual case layer 1 cannot — EF replaces a connection it finds no
//      longer usable at the start of an operation, no exception and no retry
//      involved — and it makes "held across every read" checkable instead of
//      merely argued.
//   3. PR #350 review round 3 (codex 3696801535) — layer 2 alone still did not
//      cover the WRITE. It returns, and the create transaction is established
//      only afterwards; a connection replaced in that gap leaves the INSERT
//      running on a backend that never took the lock, with layer 2's `true`
//      describing a backend that no longer exists. Verified against a real
//      Postgres: release the lock at the instant the create transaction starts
//      and provisioning completed anyway, minting an Owner from a backend
//      pg_locks showed holding nothing. So this service now OWNS the create
//      transaction and re-proves ownership as the FIRST statement inside it —
//      the same backend that runs the INSERT, by construction — and from that
//      point on a connection loss aborts the transaction instead of
//      reconnecting invisibly. Layer 2 is kept in front of it: one cheap round
//      trip that refuses early, before a password is even generated.
//
// The comment layer 2 shipped with claimed that holding the lock at the proof
// "means holding it continuously since the acquire". That was true of the
// instant the query ran and false of the create that followed; a comment that
// overstates a guarantee is how the gap survived two rounds of review.
public sealed class FirstRunAdminService(
    AppDbContext db,
    TenantContext tenant,
    CurrentUserContext currentUser,
    IAccountUserDirectory directory,
    IIdentityProvider identity,
    ILogger<FirstRunAdminService> logger)
{
    // Two-int pg_advisory_lock(int, int) form (a distinct 64-bit keyspace
    // from the single-bigint overload) so this can never collide with a
    // future single-argument advisory lock elsewhere. classId is the issue
    // number for traceability; objId leaves room for more locks under the
    // same class later without picking new arbitrary numbers.
    private const int AdvisoryLockClassId = 283;
    private const int AdvisoryLockObjectId = 1;

    public async Task<Result<FirstRunAdminOutcome>> ProvisionAsync(
        string? email, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email))
            return Result.Failure<FirstRunAdminOutcome>(Error.Validation(
                "Bootstrap.EmailRequired", "An --email is required."));

        var accountId = SeedDefaults.AccountId;

        // The account itself is migration-baked and should always exist once
        // the schema is current (MigrateAsync above already ran) — this is
        // defense-in-depth against a hand-rolled/partially-restored schema,
        // not the expected path. Read-only, so it stays outside the lock.
        var accountExists = await db.Accounts
            .IgnoreQueryFilters()
            .AnyAsync(a => a.Id == accountId, ct);
        if (!accountExists)
            return Result.Failure<FirstRunAdminOutcome>(Error.Validation(
                "Bootstrap.AccountMissing",
                "The default account does not exist. This should never happen against a schema this " +
                "command's own migrate step just brought current — check the migration history."));

        // Pin the connection open for the WHOLE critical section: a
        // session-scoped advisory lock lives on the physical connection, and
        // EF Core otherwise opens/closes a connection per operation — if it
        // closed between the lock and CreateUserAsync's own work, the lock
        // would silently release early and the guard would do nothing.
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            // ONE attempt for the entire lock -> check -> create region (see the
            // class comment). Being inside an execution strategy suspends
            // retries for every EF operation nested below, which is exactly the
            // point: a read that reconnects mid-region would leave the advisory
            // lock behind on a dead connection. Acquiring the lock is inside
            // the region too — a transient blip there fails the command rather
            // than silently re-acquiring on a connection the checks below might
            // not end up on.
            return await SingleAttemptExecution.RunAsync(db.Database, async () =>
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_lock({AdvisoryLockClassId}, {AdvisoryLockObjectId})", ct);
                try
                {
                    return await ProvisionUnderLockAsync(accountId, email.Trim(), ct);
                }
                finally
                {
                    // Always attempt the unlock, even on a cancelled/failed
                    // provision — CancellationToken.None so a caller's
                    // cancellation can't also skip releasing the lock and strand
                    // every subsequent invocation behind it for the rest of the
                    // session's lifetime.
                    //
                    // Best-effort only (PR #339 review): the lock is
                    // SESSION-scoped on THIS pinned connection, so losing the
                    // connection or session releases it automatically — the
                    // explicit unlock is cleanup, not a correctness requirement.
                    // An exception here (e.g. the connection drops right after
                    // ProvisionUnderLockAsync's commit) must never replace a
                    // successful Result: the one-time generated password lives
                    // nowhere else, and a retry would just observe the
                    // already-created Owner and no-op, stranding the operator
                    // behind break-glass recovery. Swallowed and logged instead
                    // of rethrown; a genuine ProvisionUnderLockAsync failure is
                    // unaffected — it already returned/threw before this runs.
                    await TryCleanupAsync(
                        () => db.Database.ExecuteSqlInterpolatedAsync(
                            $"SELECT pg_advisory_unlock({AdvisoryLockClassId}, {AdvisoryLockObjectId})",
                            CancellationToken.None),
                        "advisory unlock");
                }
            });
        }
        finally
        {
            // Same reasoning as the unlock above: closing an already-broken
            // connection can itself throw, and that must not suppress the
            // outcome computed above either.
            await TryCleanupAsync(
                () => db.Database.CloseConnectionAsync(),
                "connection close");
        }
    }

    // Runs best-effort post-commit cleanup: on failure, logs and swallows
    // rather than letting the exception replace the caller's real result.
    // Never logs anything derived from the temporary password — this only
    // ever wraps lock/connection plumbing, not provisioning itself.
    private async Task TryCleanupAsync(Func<Task> cleanup, string what)
    {
        try
        {
            await cleanup();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "First-run admin provisioning: {What} cleanup failed after the provisioning " +
                "outcome was already determined; ignoring (the advisory lock is session-scoped " +
                "and releases automatically once the connection/session is gone).",
                what);
        }
    }

    private async Task<Result<FirstRunAdminOutcome>> ProvisionUnderLockAsync(
        Guid accountId, string email, CancellationToken ct)
    {
        // Idempotency: an Owner already existing in the default account means
        // first-run provisioning already happened. Re-checked HERE (not just
        // by the caller before the lock) — this is the read the lock exists
        // to make safe: only one concurrent invocation can be past this point
        // at a time, so whichever one loses the race for the lock always sees
        // the winner's already-committed Owner.
        // #532 — scoped at the query. GetUsersInRoleAsync loaded every Owner in
        // every farm and post-filtered in memory: correct while one farm
        // existed, an O(all farms) cross-tenant read once several do.
        var owners = await directory.FindByAccountRoleAsync(accountId, Roles.Owner, ct);
        if (owners.Count > 0)
            return Result.Success(FirstRunAdminOutcome.AlreadyProvisioned());

        // Conflict check BEFORE mutating anything, same shape as
        // DatabaseSeeder's old cross-account guard: don't hijack an existing
        // email and don't crash — a clear fail-loud message instead.
        // #532 — SCOPED TO THIS ACCOUNT. This check used to be global, and the
        // message below even said "already exists under a different account" —
        // which is now the supported case, not a conflict. Left global, it would
        // refuse to provision farm 2's Owner whenever farm 1 already used that
        // address, blocking the very thing epic #530 decision 3 exists to allow;
        // and #533's provision-account reuses this same Owner-creation core, so
        // it would have inherited the refusal.
        var conflictingUser = await directory.FindByAccountEmailAsync(accountId, email, ct);
        if (conflictingUser is not null)
            return Result.Failure<FirstRunAdminOutcome>(Error.Validation(
                "Bootstrap.EmailInUse",
                $"A user with email '{email}' already exists in this account but holds " +
                "no Owner role. Assign the Admin role via the Users page, or choose a different --email."));

        // Gate A (PR #350 review round 2, codex 3696740950). Everything above is
        // a READ, and the only thing that makes those reads safe is the advisory
        // lock being on the connection that ran them. Prove it rather than
        // assume it: a `true` here means THIS backend held the lock at the
        // instant this statement ran, so the two reads above can be trusted.
        //
        // Note precisely what this does and does NOT establish. It is an
        // observation about one instant, not a continuity guarantee: it says
        // nothing about the connection state at the moment of the write, which
        // is why gate B below exists. Kept anyway — it is one cheap round trip,
        // it turns the common case into a clean refusal before a password is
        // even generated, and defence in depth is the right posture when the
        // failure mode is an extra farm Owner.
        //
        // Fail CLOSED. A refused bootstrap-admin costs the operator a re-run,
        // and a re-run is idempotent — it either no-ops on an Owner that now
        // exists or provisions cleanly. Proceeding unguarded costs a second
        // farm Owner with its own password, which nothing in the app can undo.
        if (!await HoldsProvisioningLockAsync(ct))
            return LockLost();

        // Handlers/audit need the tenant, which is unresolved outside an HTTP
        // request — resolve it to the default account for this scope (mirrors
        // AdminRecoveryService and the demo/simulation seeders).
        tenant.Resolve(accountId);

        // #500 — and the ACTOR: IAuditWriter fails closed on an unresolved one.
        // This verb creates the FIRST Owner, so there is no human to attribute
        // it to — not even the user being created, who does not exist yet. It
        // declares the non-person it is instead of falling into the old
        // "(unresolved)" placeholder.
        currentUser.ResolveSystemActor(SystemActors.BootstrapAdmin);

        var password = TemporaryPassword.Generate();

        // Gate B (PR #350 review round 3, codex 3696801535) — the one that
        // actually covers the write.
        //
        // Gate A returns, and only THEN does the create transaction get
        // established. In that gap EF may find the pinned connection no longer
        // usable and silently replace it — RelationalConnection reopens a
        // connection that is not Open before it does anything else, with no
        // exception thrown and no retry to intercept — so gate A's answer can
        // be about a backend that no longer exists while the INSERT runs on a
        // fresh one that never acquired the lock. Round 2's comment claimed
        // "holding it now means holding it continuously since the acquire";
        // that was true of the instant the query ran and false of the create
        // that followed it, and the overstatement is why the gap survived.
        //
        // So THIS service owns the transaction (AmbientTransaction.RunAsync's
        // owned path) and re-proves ownership as the first statement inside it.
        // Two properties follow, and only the pair is sufficient:
        //
        //   * the proof now runs on the very backend that will run the INSERT,
        //     because both are statements in one transaction; and
        //   * once a transaction is open, a connection loss can no longer be
        //     invisible — EF cannot swap the connection without abandoning the
        //     transaction, so the loss surfaces as a hard failure instead of a
        //     silent reconnect, and nothing is committed.
        //
        // IdentityProvider.CreateUserAsync then JOINS this transaction instead
        // of opening its own (AmbientTransaction's ambient path, exactly as it
        // does under IdempotencyMiddleware's request-wide transaction #307), so
        // its user row, role assignment and audit row commit or roll back with
        // the proof. Deliberately not pushed into CreateUserAsync itself: it is
        // shared with the Users page and the seeders, and a bootstrap-only
        // advisory-lock check has no business running for them.
        return await AmbientTransaction.RunAsync(db.Database, async (transaction, token) =>
        {
            if (!await HoldsProvisioningLockAsync(token))
            {
                await transaction.RollbackAsync(token);
                return LockLost();
            }

            var created = await identity.CreateUserAsync(
                accountId, email, password, Roles.Owner,
                name: "Administrator", mustChangePassword: true, ct: token);
            if (created.IsFailure)
            {
                // Explicit, because CreateUserAsync merely joined this
                // transaction — its own scope commits and rolls back nothing,
                // so a partially applied create (user inserted, role assignment
                // rejected) would otherwise ride out on our commit.
                await transaction.RollbackAsync(token);
                return Result.Failure<FirstRunAdminOutcome>(created.Error);
            }

            await transaction.CommitAsync(token);
            return Result.Success(FirstRunAdminOutcome.Provisioned(email, accountId, password));
        }, ct);
    }

    private static Result<FirstRunAdminOutcome> LockLost() =>
        Result.Failure<FirstRunAdminOutcome>(Error.Conflict(
            "Bootstrap.LockLost",
            "The first-run provisioning advisory lock is no longer held by this database session, " +
            "so the 'no Owner yet' check above cannot be trusted (the connection was most likely " +
            "replaced mid-run). Refusing to create an Owner unguarded — re-run bootstrap-admin; " +
            "it is idempotent and creates nothing if an Owner now exists."));

    // Does THIS backend currently hold the provisioning advisory lock? Asked of
    // pg_locks rather than tracked in a field, because the thing that can go
    // wrong is precisely the connection being swapped underneath us — only
    // Postgres knows. `pid = pg_backend_pid()` is evaluated server-side in the
    // same statement, so the answer is about whatever session EF just used, and
    // a concurrent invocation holding the lock can never be mistaken for us.
    // objsubid = 2 is the two-int pg_advisory_lock(int, int) keyspace (the
    // single-bigint overload records objsubid = 1).
    private Task<bool> HoldsProvisioningLockAsync(CancellationToken ct) =>
        db.Database.SqlQuery<bool>(
            $"""
             SELECT EXISTS (
                 SELECT 1 FROM pg_locks
                 WHERE locktype = 'advisory'
                   AND classid = {AdvisoryLockClassId}::oid
                   AND objid = {AdvisoryLockObjectId}::oid
                   AND objsubid = 2
                   AND pid = pg_backend_pid()
                   AND granted) AS "Value"
             """).SingleAsync(ct);
}

public sealed record FirstRunAdminOutcome(
    bool WasAlreadyProvisioned, string? Email, Guid? AccountId, string? TemporaryPassword)
{
    public static FirstRunAdminOutcome AlreadyProvisioned() => new(true, null, null, null);

    public static FirstRunAdminOutcome Provisioned(string email, Guid accountId, string password) =>
        new(false, email, accountId, password);
}
