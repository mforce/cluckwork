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
// pg_advisory_xact_lock): CreateUserAsync opens its OWN transaction, and EF
// Core does not support beginning a transaction while one is already active
// on the same context, so a transaction-scoped lock can't wrap both this
// method's read AND that nested transaction — a session lock, held for as
// long as this method keeps the connection explicitly open (OpenConnectionAsync's
// ref-count), spans both cleanly. A concurrent second invocation blocks on
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
// pg_locks showed the lock held by nobody. Two layers close it, and both fail
// CLOSED (a failed bootstrap-admin is re-runnable and idempotent; a second
// farm Owner is not undoable):
//
//   1. The whole lock -> check -> create region runs as ONE non-replayed
//      attempt (SingleAttemptExecution). Inside an execution strategy every
//      nested EF operation is suspended, so no read can be retried onto a
//      fresh connection behind our back — a transient failure mid-region
//      surfaces as an error instead. Chosen over "reacquire the lock and
//      re-run the checks after a reconnect" because that is an implicit loop
//      whose correctness is hard to prove, and it would have to interleave
//      with CreateUserAsync's own transaction; this is a straight line.
//   2. Immediately before the create, lock ownership is PROVEN rather than
//      assumed: pg_locks is asked whether THIS backend still holds it. That
//      covers the residual case layer 1 cannot — EF reopens a connection it
//      finds Broken at the start of an operation, no exception and no retry
//      involved — and it is what makes "held across every read AND the
//      create" checkable instead of merely argued.
public sealed class FirstRunAdminService(
    AppDbContext db,
    TenantContext tenant,
    UserManager<ApplicationUser> userManager,
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
        var owners = await userManager.GetUsersInRoleAsync(Roles.Owner);
        if (owners.Any(u => u.AccountId == accountId))
            return Result.Success(FirstRunAdminOutcome.AlreadyProvisioned());

        // Conflict check BEFORE mutating anything, same shape as
        // DatabaseSeeder's old cross-account guard: don't hijack an existing
        // email and don't crash — a clear fail-loud message instead.
        var normalizedEmail = userManager.NormalizeEmail(email);
        var conflicting = await db.Users
            .Where(u => u.NormalizedEmail == normalizedEmail)
            .Select(u => new { u.AccountId })
            .FirstOrDefaultAsync(ct);
        if (conflicting is not null)
            return Result.Failure<FirstRunAdminOutcome>(Error.Validation(
                "Bootstrap.EmailInUse",
                conflicting.AccountId == accountId
                    ? $"A user with email '{email}' already exists in the default account but holds " +
                      "no Owner role. Assign the Admin role via the Users page, or choose a different --email."
                    : $"A user with email '{email}' already exists under a different account."));

        // The gate (PR #350 review round 2, codex 3696740950). Everything above
        // is a READ; this is the point of no return, and the only thing that
        // makes the two reads above safe is the advisory lock still being on
        // the connection that is about to do the write. Prove it — a `true`
        // here means THIS backend holds it, and since nothing but the acquire
        // above ever takes it on this session, holding it now means holding it
        // continuously since the acquire; a replaced connection is a backend
        // that never took it, so the answer is `false`.
        //
        // Fail CLOSED. A refused bootstrap-admin costs the operator a re-run,
        // and a re-run is idempotent — it either no-ops on an Owner that now
        // exists or provisions cleanly. Proceeding unguarded costs a second
        // farm Owner with its own password, which nothing in the app can undo.
        if (!await HoldsProvisioningLockAsync(ct))
            return Result.Failure<FirstRunAdminOutcome>(Error.Conflict(
                "Bootstrap.LockLost",
                "The first-run provisioning advisory lock is no longer held by this database session, " +
                "so the 'no Owner yet' check above cannot be trusted (the connection was most likely " +
                "replaced mid-run). Refusing to create an Owner unguarded — re-run bootstrap-admin; " +
                "it is idempotent and creates nothing if an Owner now exists."));

        // Handlers/audit need the tenant, which is unresolved outside an HTTP
        // request — resolve it to the default account for this scope (mirrors
        // AdminRecoveryService and the demo/simulation seeders).
        tenant.Resolve(accountId);

        var password = TemporaryPassword.Generate();
        var created = await identity.CreateUserAsync(
            accountId, email, password, Roles.Owner,
            name: "Administrator", mustChangePassword: true, ct: ct);
        if (created.IsFailure)
            return Result.Failure<FirstRunAdminOutcome>(created.Error);

        return Result.Success(FirstRunAdminOutcome.Provisioned(email, accountId, password));
    }

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
