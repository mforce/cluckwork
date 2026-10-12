using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Cluckwork.Api.Middleware;

// #307 — a database-coordinated claim/lease protocol, replacing the old
// process-local-lock + execute + insert flow (#289's stripe). That flow could
// never be correct across replicas: two instances can both miss the same key,
// both execute the mutation and its side effects, and only contend when they
// separately try to insert the cached response — a unique index on the
// RESPONSE never made the business mutation exactly-once.
//
// Protocol, scoped by (AccountId, EndpointHash, IdempotencyKeyHash):
//   1. CLAIM — atomically insert a fresh row (Status=InProgress, a lease) or,
//      on conflict, STEAL an existing row whose lease has expired (presumed
//      abandoned) and whose RequestHash matches ours. A caller that can do
//      neither (hash mismatch, or a live competing lease) never executes.
//   2. EXECUTE — run the operation inside ONE ambient DB transaction (opened
//      on the SAME scoped AppDbContext the handler's own repositories/
//      UnitOfWork use — see UnitOfWork.ExecuteInTransactionAsync's #307
//      reentrancy note) so the domain mutation and the completion record are
//      the SAME atomic unit.
//   3. PUBLISH — on an explicit success, a GUARDED update
//      (WHERE LeaseOwner = our attempt's token) flips the claim to
//      Completed with the response payload, then commits. If the guard
//      fails (our lease was stolen while we were still executing — the
//      claimant is not proven dead, only presumed), we ROLL BACK our own
//      mutation instead of letting it become durable, and replay whatever
//      the authoritative attempt eventually publishes. This is how the
//      protocol stays exactly-once even under a genuine double-execution
//      race, not merely "at most one cached response": at most one
//      COMMIT ever wins, because it happens inside the same transaction the
//      mutation is in, guarded by the SAME token check.
//
// This is the closure for the "steal presumes dead, not proven dead" trap
// (owner's design-decision comment on #307): a crash at ANY point before
// step 3's commit leaves NOTHING durable (Postgres rolls the whole
// transaction back on connection loss), so "mutation committed but the
// completion record was never published" — the state a naive steal-and-
// re-execute would double — cannot exist.
//
// #809 — extracted from IdempotencyMiddleware so the MCP write tool runs the
// same protocol. Success is the operation's own verdict, never "it returned":
// a failure is not cached, so a domain refusal (a depleted flock) does not
// replay forever after the farm fixes it.
public sealed class IdempotentUnit(AppDbContext db, IOptions<IdempotencyOptions> options, ILogger<IdempotentUnit> logger)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(40);

    public async Task<IdempotentResult> RunAsync(
        IdempotencyScope scope, Func<Task<IdempotentResponse>> execute, CancellationToken ct)
    {
        var ownerToken = Guid.NewGuid();
        var claimStartedAt = DateTimeOffset.UtcNow;
        var leaseDuration = TimeSpan.FromSeconds(Math.Max(1, options.Value.LeaseDurationSeconds));
        var waitDeadline = claimStartedAt + TimeSpan.FromSeconds(Math.Max(1, options.Value.MaxWaitSeconds));

        // --- 1. CLAIM (bounded poll: wait out a LIVE competing lease; a dead
        // one is stolen the moment its lease expires) ---
        while (true)
        {
            var now = DateTimeOffset.UtcNow;
            var attempt = await TryClaimOrInspectAsync(scope, ownerToken, now + leaseDuration, now, ct);

            if (attempt.Kind == ClaimKind.Claimed) break;
            if (attempt.Kind == ClaimKind.HashConflict) return new IdempotentResult.KeyReused();
            if (attempt.Kind == ClaimKind.Completed) return new IdempotentResult.Replayed(Stored(attempt.Record!));

            // LiveLease (someone else genuinely still holds it) or Retry (the
            // row vanished mid-attempt, e.g. a concurrent release) — either
            // way, back off briefly and re-evaluate rather than executing.
            if (DateTimeOffset.UtcNow >= waitDeadline) return new IdempotentResult.InProgress();

            await Task.Delay(PollInterval, ct);
        }

        // --- 2. EXECUTE, under one ambient transaction on THIS request's
        // scoped AppDbContext, so the mutation and the completion record
        // (step 3) are one atomic unit. ---
        //
        // #269 — EnableRetryOnFailure forbids a manually-begun transaction
        // from being touched outside its own database.CreateExecutionStrategy()
        // .ExecuteAsync — EF throws InvalidOperationException the instant
        // anything else runs against this DbContext while the transaction is
        // open, retryable failure or not (see PostgresDbContextConfigurator).
        // Since the WHOLE point of this transaction is to cover the operation
        // too (its own SaveChanges and step 3's PUBLISH have to be the SAME
        // atomic unit — the #307 comment above), this block has to sit inside
        // an execution strategy.
        //
        // It runs there EXACTLY ONCE (SingleAttemptExecution). The operation
        // is not replayable, and no amount of database-state re-checking
        // makes it so:
        //
        //   * it consumes single-use, non-database claim state: the #308/#360
        //     step-up grant is consumed by CreateUserHandler,
        //     SetUserPasswordHandler, or ChangeUserRoleHandler, so a second
        //     pass over the user mutation is denied outright;
        //   * for a state transition (daily-entry submit, order confirm) a
        //     second pass observes the FIRST pass's committed transition and
        //     answers 422 — a 4xx saying the request was invalid, for a
        //     request that in fact succeeded, and one no client will retry.
        //
        // What makes a transient mid-request failure recoverable here is not
        // a server-side replay but the CLIENT retrying with the same key —
        // which is precisely the guarantee #307 built. And for the narrow
        // ambiguous-commit case (Postgres committed, the acknowledgment is
        // what was lost) the catch below does better than an error: our own
        // claim is now Completed, so the response this attempt published is
        // returned instead.
        try
        {
            IdempotentResult? result = null;
            await SingleAttemptExecution.RunAsync(db.Database, async () =>
            {
                var transaction = await db.Database.BeginTransactionAsync(ct);
                // #307 review — RollbackAsync/CommitAsync alone do NOT return the
                // underlying connection to the ADO.NET pool; only DisposeAsync does. The
                // steal-loss branch below needs to release the connection BEFORE it starts
                // polling (which can run for up to Idempotency:MaxWaitSeconds), so
                // disposal happens explicitly there too — this guard makes calling it from
                // both that branch AND the unconditional `finally` safe (idempotent)
                // without relying on IDbContextTransaction's own dispose being a no-op the
                // second time.
                var transactionDisposed = false;
                async Task DisposeTransactionOnceAsync()
                {
                    if (transactionDisposed) return;
                    transactionDisposed = true;
                    await transaction.DisposeAsync();
                }
                try
                {
                    var response = await execute();

                    if (response.Succeeded)
                    {
                        // --- 3. PUBLISH, guarded by our attempt's token. ---
                        var published = await db.Database.ExecuteSqlInterpolatedAsync($"""
                            UPDATE idempotency_records SET
                                "Status" = {(int)IdempotencyStatus.Completed},
                                "StatusCode" = {response.StatusCode},
                                "ContentType" = {response.ContentType},
                                "ResponseBody" = {response.Body},
                                "CompletedAt" = {DateTimeOffset.UtcNow}
                            WHERE "AccountId" = {scope.AccountId} AND "EndpointHash" = {scope.EndpointHash}
                              AND "IdempotencyKeyHash" = {scope.KeyHash} AND "LeaseOwner" = {ownerToken}
                              AND "Status" = {(int)IdempotencyStatus.InProgress}
                            """, CancellationToken.None);

                        if (published == 1)
                        {
                            // Persist even if the client disconnects after the operation
                            // committed its side effects — otherwise a retry with the
                            // same key could execute the operation again.
                            await transaction.CommitAsync(CancellationToken.None);
                            result = new IdempotentResult.Executed(response);
                            return;
                        }

                        // Our lease was stolen mid-execution: someone else is now the
                        // authoritative claimant. Our mutation must NEVER become
                        // durable (it may duplicate whatever they run/ran) —
                        // roll back, then converge on whatever THEY publish.
                        await transaction.RollbackAsync(CancellationToken.None);
                        // Dispose (release the connection) BEFORE the poll, not after —
                        // see the comment on transactionDisposed above.
                        await DisposeTransactionOnceAsync();
                        var winner = await WaitForCompletionAsync(scope, waitDeadline, ct);
                        result = winner is null
                            ? new IdempotentResult.InProgress()
                            : new IdempotentResult.Replayed(Stored(winner));
                        return;
                    }

                    // A failure: nothing is cached (matches the pre-#307 contract), and
                    // the claim is released immediately so a corrected retry with the
                    // same key does not have to wait out the lease.
                    await transaction.RollbackAsync(CancellationToken.None);
                    await ReleaseClaimAsync(scope, ownerToken, CancellationToken.None);
                    result = new IdempotentResult.Executed(response);
                }
                catch
                {
                    // Transaction-scoped cleanup only; the claim is dealt with
                    // in the outer catch, once the connection is back in the
                    // pool and a probe read is possible.
                    try { await transaction.RollbackAsync(CancellationToken.None); }
                    catch { /* connection likely gone; nothing durable to undo */ }
                    throw;
                }
                finally
                {
                    await DisposeTransactionOnceAsync();
                }
            });
            return result!;
        }
        catch
        {
            // A failed SaveChangesAsync (e.g. the DbUpdateConcurrencyException a
            // parallel-race test drives) leaves its entities tracked as Modified
            // — EF does not detach them on failure. UseExceptionHandler is
            // registered BEFORE IdempotencyMiddleware, so it re-executes the WHOLE
            // downstream pipeline on the SAME scoped AppDbContext. Since #345 that
            // re-execution no longer re-enters the claim protocol (see the
            // IExceptionHandlerFeature skip in the middleware), so nothing here
            // flushes the tracker any more — but ANY future re-executed middleware
            // or /error handler that touched this DbContext would otherwise re-flush
            // the SAME stale entities and re-throw the SAME exception, uncaught,
            // letting it escape to the client instead of the mapped 409. Keep the
            // scope's tracker clean before unwinding. (Scoped to ONE request's own
            // context — never a shared/long-lived one; see SingleAttemptExecution
            // for why a blanket Clear() is wrong in IdentityProvider.)
            db.ChangeTracker.Clear();

            // #269 review — the failure may have been a LOST ACKNOWLEDGMENT of a
            // commit that actually landed (the ambiguous commit). Because the
            // mutation and the PUBLISH are ONE transaction (#307), the claim's own
            // status is the authoritative answer to "did my work become durable?":
            // Completed means it did, and the response this attempt published is
            // sitting right there. Return it rather than reporting a failure for an
            // operation that succeeded — and rather than re-running it, which would
            // re-consume a single-use step-up grant and make a state transition
            // answer 422. Best-effort: if the connection is genuinely gone the
            // probe throws and we fall through to the honest error, which the
            // client then retries with the same key and replays anyway.
            try
            {
                var completed = await ReadCompletedClaimAsync(scope, ownerToken, CancellationToken.None);
                if (completed is not null)
                    return new IdempotentResult.Executed(Stored(completed));
            }
            catch { /* probe failed; fall through to the error below */ }

            // Nothing of ours is durable. Release OUR claim (best-effort) so a
            // corrected retry from the CLIENT doesn't have to wait out the lease.
            try { await ReleaseClaimAsync(scope, ownerToken, CancellationToken.None); }
            catch { /* best-effort — an abandoned claim still recovers via lease expiry */ }
            throw;
        }
    }

    private static IdempotentResponse Stored(IdempotencyRecord record) => new(
        Succeeded: true, record.StatusCode ?? StatusCodes.Status200OK, record.ContentType, record.ResponseBody ?? string.Empty);

    // The claim this attempt owns, but only once it has actually been published.
    // LeaseOwner is this attempt's own single-use token, so a match cannot be
    // anyone else's work.
    private Task<IdempotencyRecord?> ReadCompletedClaimAsync(
        IdempotencyScope scope, Guid ownerToken, CancellationToken ct) =>
        db.IdempotencyRecords.AsNoTracking().FirstOrDefaultAsync(r =>
            r.AccountId == scope.AccountId && r.EndpointHash == scope.EndpointHash
            && r.IdempotencyKeyHash == scope.KeyHash && r.LeaseOwner == ownerToken
            && r.Status == IdempotencyStatus.Completed, ct);

    private enum ClaimKind { Claimed, HashConflict, Completed, LiveLease, Retry }

    private sealed record ClaimAttempt(ClaimKind Kind, IdempotencyRecord? Record = null);

    // Attempts to become (or remain) the lease holder for this key. Returns
    // Claimed when the caller now owns the lease (ownerToken); otherwise
    // reports why not, so the caller can conflict, replay, or wait.
    private async Task<ClaimAttempt> TryClaimOrInspectAsync(
        IdempotencyScope scope, Guid ownerToken, DateTimeOffset leaseExpiresAt, DateTimeOffset now, CancellationToken ct)
    {
        // A replay, or a concurrent claim that won the race, is an expected outcome, so it
        // is a DO NOTHING rather than a unique violation EF would log twice at Error (#1230).
        // Raw SQL gets no retry of its own; the execution strategy replays it as it replayed
        // the SaveChanges this replaced, and a replay after a lost acknowledgment finds our
        // own row below (RetryBoundaryTests).
        var claimed = await db.Database.CreateExecutionStrategy().ExecuteAsync(() =>
            db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO idempotency_records ("Id", "AccountId", "EndpointHash", "IdempotencyKeyHash", "RequestHash",
                    "Status", "LeaseOwner", "LeaseExpiresAt", "CreatedAt")
                VALUES ({Guid.NewGuid()}, {scope.AccountId}, {scope.EndpointHash}, {scope.KeyHash}, {scope.RequestHash},
                    {(int)IdempotencyStatus.InProgress}, {ownerToken}, {leaseExpiresAt}, {now})
                ON CONFLICT ("AccountId", "EndpointHash", "IdempotencyKeyHash") DO NOTHING
                """, ct));
        if (claimed == 1) return new ClaimAttempt(ClaimKind.Claimed);
        logger.LogDebug("Idempotency key already claimed; inspecting the existing claim.");

        // Steal: only a row whose lease has expired AND whose RequestHash
        // matches ours (an unrelated payload under the same key is always a
        // conflict, never something to run over). AccountId is included even
        // though the unique index already scopes by tenant — defense in
        // depth for this hand-written predicate (#313's lesson: a raw-SQL
        // WHERE must carry its own tenant filter).
        var stolen = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE idempotency_records SET "LeaseOwner" = {ownerToken}, "LeaseExpiresAt" = {leaseExpiresAt}
            WHERE "AccountId" = {scope.AccountId} AND "EndpointHash" = {scope.EndpointHash}
              AND "IdempotencyKeyHash" = {scope.KeyHash} AND "RequestHash" = {scope.RequestHash}
              AND "Status" = {(int)IdempotencyStatus.InProgress} AND "LeaseExpiresAt" < {now}
            """, ct);
        if (stolen == 1) return new ClaimAttempt(ClaimKind.Claimed);

        var existing = await db.IdempotencyRecords.AsNoTracking().FirstOrDefaultAsync(r =>
            r.AccountId == scope.AccountId && r.EndpointHash == scope.EndpointHash
            && r.IdempotencyKeyHash == scope.KeyHash, ct);

        if (existing is null) return new ClaimAttempt(ClaimKind.Retry);
        // An empty RequestHash means "unknown" — the row was backfilled by the
        // pre-#245 AtomicIdempotencyClaims migration from a row written under the
        // pre-#307 schema, which never recorded a hash at all. No REAL request hash
        // is ever the empty string (sha256 of even a zero-byte body is a 64-char hex
        // string), so "" is an unambiguous, collision-free sentinel: treat it as
        // matching so a legacy completed row still replays instead of 409ing
        // forever, rather than trying to reconstruct a hash nothing recorded.
        //
        // #245 — the squash HAS now retired every pre-#307 row (that migration is
        // gone, and every database carrying such rows was dropped and recreated),
        // so this branch is unreachable in practice. Left in place deliberately as
        // an inert guard rather than removed in the squash commit, which changes no
        // behaviour; deleting it is a separate, reviewable change.
        if (existing.RequestHash.Length > 0 && existing.RequestHash != scope.RequestHash)
            return new ClaimAttempt(ClaimKind.HashConflict, existing);
        if (existing.Status == IdempotencyStatus.Completed) return new ClaimAttempt(ClaimKind.Completed, existing);
        // #269 review — the row is OURS. ownerToken is a fresh Guid minted for
        // this one attempt, so nothing else can have written it: what happened
        // is that our own INSERT (or steal UPDATE) above already committed and
        // its acknowledgment was lost, and EF's retrying execution strategy —
        // which wraps SaveChanges/ExecuteSql on its own, outside any user
        // transaction — replayed it into the unique index. We hold the lease;
        // reporting a competing LiveLease here would make the attempt poll
        // itself out to the deadline and give up without ever executing.
        if (existing.LeaseOwner == ownerToken) return new ClaimAttempt(ClaimKind.Claimed);
        return new ClaimAttempt(ClaimKind.LiveLease);
    }

    // Best-effort: releases OUR claim (only if we still own it) so a
    // corrected retry doesn't wait out the lease. If this races a steal and
    // loses, the guard just makes it a no-op — the new owner is unaffected.
    private Task ReleaseClaimAsync(IdempotencyScope scope, Guid ownerToken, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM idempotency_records
            WHERE "AccountId" = {scope.AccountId} AND "EndpointHash" = {scope.EndpointHash}
              AND "IdempotencyKeyHash" = {scope.KeyHash} AND "LeaseOwner" = {ownerToken}
              AND "Status" = {(int)IdempotencyStatus.InProgress}
            """, ct);

    // Polls (bounded by deadline) for the row to reach Completed — used only
    // after THIS attempt already lost its own publish race (see the steal-loss
    // branch above): it is purely watching for the authoritative attempt to
    // finish, never trying to reclaim the key itself.
    private async Task<IdempotencyRecord?> WaitForCompletionAsync(
        IdempotencyScope scope, DateTimeOffset deadline, CancellationToken ct)
    {
        while (true)
        {
            var existing = await db.IdempotencyRecords.AsNoTracking().FirstOrDefaultAsync(r =>
                r.AccountId == scope.AccountId && r.EndpointHash == scope.EndpointHash
                && r.IdempotencyKeyHash == scope.KeyHash, ct);
            if (existing is { Status: IdempotencyStatus.Completed }) return existing;
            // #307 review — the row can vanish out from under a waiter: the
            // authoritative claimant we're watching may itself fail (a failure
            // releases its claim via ReleaseClaimAsync). Nobody is going to
            // complete a claim that no longer exists, and reclaiming it is not
            // this function's job (that's the outer claim loop's Retry
            // handling) — so give up immediately instead of polling out the
            // rest of the bounded wait for nothing.
            if (existing is null) return null;
            if (DateTimeOffset.UtcNow >= deadline) return null;
            await Task.Delay(PollInterval, ct);
        }
    }
}

// Which claim an operation runs under. EndpointHash names the operation and
// KeyHash the caller's key (already scoped to a user where the adapter wants
// that); RequestHash fingerprints the arguments, so a reused key is refused.
public sealed record IdempotencyScope(Guid AccountId, string EndpointHash, string KeyHash, string RequestHash);

// What the operation produced. Only Succeeded is published and replayed;
// StatusCode and ContentType are stored for the HTTP adapter's replay.
public sealed record IdempotentResponse(bool Succeeded, int StatusCode, string? ContentType, string Body);

public abstract record IdempotentResult
{
    private IdempotentResult() { }

    // This attempt ran the operation, or its own commit landed and only the
    // acknowledgment was lost.
    public sealed record Executed(IdempotentResponse Response) : IdempotentResult;

    // Another attempt with the same key and arguments already succeeded.
    public sealed record Replayed(IdempotentResponse Response) : IdempotentResult;

    // The key was used before with different arguments; nothing ran.
    public sealed record KeyReused : IdempotentResult;

    // Another attempt still holds the key; nothing ran.
    public sealed record InProgress : IdempotentResult;
}
