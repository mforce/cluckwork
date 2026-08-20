namespace Cluckwork.Api.Middleware;

using System.Security.Cryptography;
using System.Text;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

// #307 — a database-coordinated claim/lease protocol, replacing the old
// process-local-lock + execute + insert flow (#289's stripe, kept only as a
// comment below for history). That flow could never be correct across
// replicas: two instances can both miss the same key, both execute the
// mutation and its side effects, and only contend when they separately try
// to insert the cached response — a unique index on the RESPONSE never made
// the business mutation exactly-once.
//
// Protocol, scoped by (AccountId, EndpointHash, IdempotencyKeyHash):
//   1. CLAIM — atomically insert a fresh row (Status=InProgress, a lease) or,
//      on conflict, STEAL an existing row whose lease has expired (presumed
//      abandoned) and whose RequestHash matches ours. A caller that can do
//      neither (hash mismatch, or a live competing lease) never invokes the
//      handler.
//   2. EXECUTE — run the handler inside ONE ambient DB transaction (opened
//      on the SAME scoped AppDbContext the handler's own repositories/
//      UnitOfWork use — see UnitOfWork.ExecuteInTransactionAsync's #307
//      reentrancy note) so the domain mutation and the completion record are
//      the SAME atomic unit.
//   3. PUBLISH — on a 2xx response, a GUARDED update
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
public sealed class IdempotencyMiddleware(RequestDelegate next, IOptions<IdempotencyOptions> options)
{
    private const string HeaderName = "Idempotency-Key";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(40);

    // #165 — routes whose RESPONSE must never be cached or replayed. A record
    // stores only status/content-type/body, so caching one of these would both
    // (a) persist that body — here a live access token — in idempotency_records,
    // and (b) replay it WITHOUT Set-Cookie, handing the client a fresh access
    // token while its refresh cookie stays the old, now-revoked one: a session
    // that dies silently ~15 minutes later. Caching buys nothing here anyway —
    // a password change is self-invalidating, because a replayed request's
    // current-password no longer matches.
    private static readonly string[] ResponseNotCacheable =
    [
        "/api/v1/auth/change-password",
        // #308 — same reasoning as change-password: the response body is a
        // live, single-use step-up grant. Caching/replaying it would hand back
        // an already-consumed (or since-expired) token instead of a fresh one.
        "/api/v1/auth/step-up",
        // #336 review — logout is a pure side effect (its body is an empty 204),
        // so replaying a stored response would return "success" while revoking
        // NOTHING: the caller believes the session ended when it did not. It is
        // also idempotent by construction, so it needs no replay protection.
        //
        // Being on this list is load-bearing for a second reason: the exemption
        // is checked BEFORE the tenant gate below, and logout became able to
        // carry a bearer (#336 — it must revoke the access-token user's step-up
        // grants, not only the cookie owner's). Without this entry that bearer
        // would resolve a tenant and every authenticated logout would 400 for a
        // missing Idempotency-Key, i.e. the SPA could no longer log out.
        "/api/v1/auth/logout",
        // #532 — login and refresh both return a LIVE ACCESS TOKEN in the body.
        // Both endpoints blank their ambient principal via
        // IgnoresAmbientPrincipalAttribute, so neither resolves a tenant and
        // the gate below skips them — and being on this list is what keeps that
        // true if the marker is ever removed. Being here also means a future
        // change that resolves a tenant on either path cannot silently start
        // persisting a live credential into idempotency_records, keyed by
        // account rather than by user and replayable by anyone in that account.
        "/api/v1/auth/login",
        "/api/v1/auth/refresh",
    ];

    public async Task InvokeAsync(
        HttpContext context, AppDbContext db, TenantContext tenant, CurrentUserContext user)
    {
        // #345 — UseExceptionHandler (Program.cs) does not just render a body: on
        // an unhandled exception it rewrites Request.Path to "/error" and
        // RE-EXECUTES the whole downstream pipeline, this middleware included. On
        // that second pass the method is still the original write verb and the
        // tenant still resolves from the original request's bearer token, but the
        // PATH is now "/error" — so every path-derived decision below is made
        // about a request the client never sent. Two measured consequences:
        //
        //   (a) the ResponseNotCacheable exemption is matched on Request.Path, so
        //       it stops matching: a /auth/change-password that throws re-enters
        //       here as "/error", carries no Idempotency-Key (correctly — it is
        //       exempt), and gets this middleware's own 400
        //       "Idempotency-Key header is required" INSTEAD of the exception's
        //       real mapped status. A 500 was being served to the client as a 400.
        //
        //   (b) for a tracked write the re-entry claims a SECOND, "/error"-scoped
        //       row (EndpointHash covers the path, so it does NOT collide with
        //       the original request's claim — that one is already released by
        //       the catch below). Harmless in the happy case, but it makes
        //       rendering an error a DATABASE WRITE: when the fault IS the
        //       database, the claim INSERT throws too, the exception handler
        //       itself fails, and the client gets no ProblemDetails at all —
        //       just a dropped/500-with-no-body connection.
        //
        // Idempotency has nothing to contribute to a re-execution: the client's
        // request was already claimed, executed and released (or replayed) by the
        // first pass, and "/error" is a synthetic path no client can retry against.
        // So skip entirely — do not merely widen the exemption match.
        if (context.Features.Get<IExceptionHandlerFeature>() is not null)
        {
            await next(context);
            return;
        }

        if (!HttpMethods.IsPost(context.Request.Method)
            && !HttpMethods.IsPut(context.Request.Method)
            && !HttpMethods.IsPatch(context.Request.Method)
            && !HttpMethods.IsDelete(context.Request.Method))
        {
            await next(context);
            return;
        }

        if (ResponseNotCacheable.Any(p =>
                context.Request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
        {
            await next(context);
            return;
        }

        if (!tenant.IsResolved)
        {
            await next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(HeaderName, out var rawKey) || string.IsNullOrWhiteSpace(rawKey))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new
            {
                type = "https://cluckwork.local/problems/idempotency-key-required",
                title = "Idempotency-Key header is required for write requests.",
                status = StatusCodes.Status400BadRequest
            });
            return;
        }

        var accountId = tenant.AccountId;
        var endpointHash = Sha256($"{context.Request.Method}:{context.Request.Path}");
        // User-scope the idempotency key ONLY for the per-user /me endpoints (#45): two
        // users in one account presenting the same key on their own /me write must each
        // execute. Every other endpoint operates on shared account data and keeps the
        // account-only key, so no existing record's hash changes — a same-user retry
        // spanning a deployment still replays, with no cross-deploy re-execution window
        // on account-scoped writes (sales, payments, inventory, …).
        var userScoped = user.IsResolved
            && context.Request.Path.StartsWithSegments("/api/v1/me", StringComparison.OrdinalIgnoreCase);
        var keyHash = userScoped ? Sha256($"{user.UserId}:{rawKey}") : Sha256(rawKey.ToString());
        var requestHash = await ComputeRequestHashAsync(context.Request, context.RequestAborted);

        var ownerToken = Guid.NewGuid();
        var claimStartedAt = DateTimeOffset.UtcNow;
        var leaseDuration = TimeSpan.FromSeconds(Math.Max(1, options.Value.LeaseDurationSeconds));
        var waitDeadline = claimStartedAt + TimeSpan.FromSeconds(Math.Max(1, options.Value.MaxWaitSeconds));

        // --- 1. CLAIM (bounded poll: wait out a LIVE competing lease; a dead
        // one is stolen the moment its lease expires) ---
        while (true)
        {
            var now = DateTimeOffset.UtcNow;
            var attempt = await TryClaimOrInspectAsync(
                db, accountId, endpointHash, keyHash, requestHash, ownerToken, now + leaseDuration, now,
                context.RequestAborted);

            if (attempt.Kind == ClaimKind.Claimed) break;

            if (attempt.Kind == ClaimKind.HashConflict)
            {
                await WriteProblemAsync(context, StatusCodes.Status409Conflict,
                    "https://cluckwork.local/problems/idempotency-key-conflict",
                    "Idempotency-Key was already used with a different request.");
                return;
            }

            if (attempt.Kind == ClaimKind.Completed)
            {
                await ReplayAsync(context, attempt.Record!);
                return;
            }

            // LiveLease (someone else genuinely still holds it) or Retry (the
            // row vanished mid-attempt, e.g. a concurrent release) — either
            // way, back off briefly and re-evaluate rather than invoking the
            // handler.
            if (DateTimeOffset.UtcNow >= waitDeadline)
            {
                await WriteProblemAsync(context, StatusCodes.Status409Conflict,
                    "https://cluckwork.local/problems/idempotency-in-progress",
                    "A request with this Idempotency-Key is still being processed. Retry shortly.");
                return;
            }

            await Task.Delay(PollInterval, context.RequestAborted);
        }

        // --- 2. EXECUTE, under one ambient transaction on THIS request's
        // scoped AppDbContext, so the mutation and the completion record
        // (step 3) are one atomic unit. ---
        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();

        // #269 — EnableRetryOnFailure forbids a manually-begun transaction
        // from being touched outside its own database.CreateExecutionStrategy()
        // .ExecuteAsync — EF throws InvalidOperationException the instant
        // anything else runs against this DbContext while the transaction is
        // open, retryable failure or not (see PostgresDbContextConfigurator).
        // Since the WHOLE point of this transaction is to cover next(context)
        // too (the handler's own SaveChanges and step 3's PUBLISH have to be
        // the SAME atomic unit — the #307 comment above), this block has to
        // sit inside an execution strategy.
        //
        // It runs there EXACTLY ONCE (SingleAttemptExecution). `next(context)`
        // — the whole downstream pipeline — is not replayable, and no amount
        // of database-state re-checking makes it so:
        //
        //   * it consumes single-use, NON-database state: the #308 step-up
        //     grant is consumed in memory by CreateUserHandler /
        //     SetUserPasswordHandler, so a second pass over POST /users is
        //     denied outright;
        //   * for a state transition (daily-entry submit, order confirm) a
        //     second pass observes the FIRST pass's committed transition and
        //     answers 422 — a 4xx saying the request was invalid, for a
        //     request that in fact succeeded, and one no client will retry.
        //
        // What makes a transient mid-request failure recoverable here is not
        // a server-side replay but the CLIENT retrying with the same
        // Idempotency-Key — which is precisely the guarantee #307 built. And
        // for the narrow ambiguous-commit case (Postgres committed, the
        // acknowledgment is what was lost) the catch below does better than
        // an error: our own claim is now Completed, so the response this
        // request published is replayed instead.
        try
        {
            await SingleAttemptExecution.RunAsync(db.Database, async () =>
            {
                context.Response.Body = buffer;

                var transaction = await db.Database.BeginTransactionAsync(context.RequestAborted);
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
                    await next(context);

                    buffer.Position = 0;
                    var body = await new StreamReader(buffer).ReadToEndAsync(context.RequestAborted);
                    var statusCode = context.Response.StatusCode;
                    var contentType = context.Response.ContentType;
                    context.Response.Body = originalBody;

                    if (statusCode is >= 200 and < 300)
                    {
                        // --- 3. PUBLISH, guarded by our attempt's token. ---
                        var published = await db.Database.ExecuteSqlInterpolatedAsync($"""
                            UPDATE idempotency_records SET
                                "Status" = {(int)IdempotencyStatus.Completed},
                                "StatusCode" = {statusCode},
                                "ContentType" = {contentType},
                                "ResponseBody" = {body},
                                "CompletedAt" = {DateTimeOffset.UtcNow}
                            WHERE "AccountId" = {accountId} AND "EndpointHash" = {endpointHash}
                              AND "IdempotencyKeyHash" = {keyHash} AND "LeaseOwner" = {ownerToken}
                              AND "Status" = {(int)IdempotencyStatus.InProgress}
                            """, CancellationToken.None);

                        if (published == 1)
                        {
                            // Persist even if the client disconnects after the handler
                            // committed its side effects — otherwise a retry with the
                            // same key could execute the operation again.
                            await transaction.CommitAsync(CancellationToken.None);
                            await EmitAsync(context, statusCode, contentType, body);
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
                        var winner = await WaitForCompletionAsync(
                            db, accountId, endpointHash, keyHash, waitDeadline, context.RequestAborted);
                        if (winner is not null)
                        {
                            await ReplayAsync(context, winner);
                            return;
                        }

                        await WriteProblemAsync(context, StatusCodes.Status409Conflict,
                            "https://cluckwork.local/problems/idempotency-in-progress",
                            "A request with this Idempotency-Key is still being processed. Retry shortly.");
                        return;
                    }

                    // Non-2xx: nothing is cached (matches the pre-#307 contract), and
                    // the claim is released immediately so a corrected retry with the
                    // same key does not have to wait out the lease.
                    await transaction.RollbackAsync(CancellationToken.None);
                    await ReleaseClaimAsync(db, accountId, endpointHash, keyHash, ownerToken, CancellationToken.None);
                    await EmitAsync(context, statusCode, contentType, body);
                }
                catch
                {
                    // Transaction-scoped cleanup only; the claim is dealt with
                    // in the outer catch, once the connection is back in the
                    // pool and a probe read is possible.
                    context.Response.Body = originalBody;
                    try { await transaction.RollbackAsync(CancellationToken.None); }
                    catch { /* connection likely gone; nothing durable to undo */ }
                    throw;
                }
                finally
                {
                    await DisposeTransactionOnceAsync();
                }
            });
        }
        catch
        {
            context.Response.Body = originalBody;
            // A failed SaveChangesAsync (e.g. the DbUpdateConcurrencyException a
            // parallel-race test drives) leaves its entities tracked as Modified
            // — EF does not detach them on failure. UseExceptionHandler is
            // registered BEFORE this middleware, so it re-executes the WHOLE
            // downstream pipeline on the SAME scoped AppDbContext. Since #345 that
            // re-execution no longer re-enters the claim protocol (see the
            // IExceptionHandlerFeature skip at the top), so nothing here flushes
            // the tracker any more — but ANY future re-executed middleware or
            // /error handler that touched this DbContext would otherwise re-flush
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
            // Completed means it did, and the response this request published is
            // sitting right there. Replay it rather than reporting a failure for a
            // request that succeeded — and rather than re-running next(context),
            // which would re-consume a single-use step-up grant and make a state
            // transition answer 422. Best-effort: if the connection is genuinely
            // gone the probe throws and we fall through to the honest error, which
            // the client then retries with the same key and replays anyway.
            try
            {
                var completed = await ReadCompletedClaimAsync(
                    db, accountId, endpointHash, keyHash, ownerToken, CancellationToken.None);
                if (completed is not null)
                {
                    await ReplayAsync(context, completed);
                    return;
                }
            }
            catch { /* probe failed; fall through to the error below */ }

            // Nothing of ours is durable. Release OUR claim (best-effort) so a
            // corrected retry from the CLIENT doesn't have to wait out the lease.
            try { await ReleaseClaimAsync(db, accountId, endpointHash, keyHash, ownerToken, CancellationToken.None); }
            catch { /* best-effort — an abandoned claim still recovers via lease expiry */ }
            throw;
        }
    }

    // The claim this attempt owns, but only once it has actually been published.
    // LeaseOwner is this request's own single-use token, so a match cannot be
    // anyone else's work.
    private static Task<IdempotencyRecord?> ReadCompletedClaimAsync(
        AppDbContext db, Guid accountId, string endpointHash, string keyHash, Guid ownerToken,
        CancellationToken ct) =>
        db.IdempotencyRecords.AsNoTracking().FirstOrDefaultAsync(r =>
            r.AccountId == accountId && r.EndpointHash == endpointHash
            && r.IdempotencyKeyHash == keyHash && r.LeaseOwner == ownerToken
            && r.Status == IdempotencyStatus.Completed, ct);

    private enum ClaimKind { Claimed, HashConflict, Completed, LiveLease, Retry }

    private sealed record ClaimAttempt(ClaimKind Kind, IdempotencyRecord? Record = null);

    // Attempts to become (or remain) the lease holder for this key. Returns
    // Claimed when the caller now owns the lease (ownerToken); otherwise
    // reports why not, so the caller can conflict, replay, or wait.
    private static async Task<ClaimAttempt> TryClaimOrInspectAsync(
        AppDbContext db, Guid accountId, string endpointHash, string keyHash, string requestHash,
        Guid ownerToken, DateTimeOffset leaseExpiresAt, DateTimeOffset now, CancellationToken ct)
    {
        var fresh = new IdempotencyRecord
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            EndpointHash = endpointHash,
            IdempotencyKeyHash = keyHash,
            RequestHash = requestHash,
            Status = IdempotencyStatus.InProgress,
            LeaseOwner = ownerToken,
            LeaseExpiresAt = leaseExpiresAt,
            CreatedAt = now,
        };
        db.IdempotencyRecords.Add(fresh);
        try
        {
            await db.SaveChangesAsync(ct);
            return new ClaimAttempt(ClaimKind.Claimed);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.Entry(fresh).State = EntityState.Detached;
        }

        // Steal: only a row whose lease has expired AND whose RequestHash
        // matches ours (an unrelated payload under the same key is always a
        // conflict, never something to run over). AccountId is included even
        // though the unique index already scopes by tenant — defense in
        // depth for this hand-written predicate (#313's lesson: a raw-SQL
        // WHERE must carry its own tenant filter).
        var stolen = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE idempotency_records SET "LeaseOwner" = {ownerToken}, "LeaseExpiresAt" = {leaseExpiresAt}
            WHERE "AccountId" = {accountId} AND "EndpointHash" = {endpointHash}
              AND "IdempotencyKeyHash" = {keyHash} AND "RequestHash" = {requestHash}
              AND "Status" = {(int)IdempotencyStatus.InProgress} AND "LeaseExpiresAt" < {now}
            """, ct);
        if (stolen == 1) return new ClaimAttempt(ClaimKind.Claimed);

        var existing = await db.IdempotencyRecords.AsNoTracking().FirstOrDefaultAsync(r =>
            r.AccountId == accountId && r.EndpointHash == endpointHash && r.IdempotencyKeyHash == keyHash, ct);

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
        if (existing.RequestHash.Length > 0 && existing.RequestHash != requestHash)
            return new ClaimAttempt(ClaimKind.HashConflict, existing);
        if (existing.Status == IdempotencyStatus.Completed) return new ClaimAttempt(ClaimKind.Completed, existing);
        // #269 review — the row is OURS. ownerToken is a fresh Guid minted for
        // this one request, so nothing else can have written it: what happened
        // is that our own INSERT (or steal UPDATE) above already committed and
        // its acknowledgment was lost, and EF's retrying execution strategy —
        // which wraps SaveChanges/ExecuteSql on its own, outside any user
        // transaction — replayed it into the unique index. We hold the lease;
        // reporting a competing LiveLease here would make the request poll
        // itself out to the deadline and 409 without ever invoking the handler.
        if (existing.LeaseOwner == ownerToken) return new ClaimAttempt(ClaimKind.Claimed);
        return new ClaimAttempt(ClaimKind.LiveLease);
    }

    // Best-effort: releases OUR claim (only if we still own it) so a
    // corrected retry doesn't wait out the lease. If this races a steal and
    // loses, the guard just makes it a no-op — the new owner is unaffected.
    private static Task ReleaseClaimAsync(
        AppDbContext db, Guid accountId, string endpointHash, string keyHash, Guid ownerToken, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM idempotency_records
            WHERE "AccountId" = {accountId} AND "EndpointHash" = {endpointHash}
              AND "IdempotencyKeyHash" = {keyHash} AND "LeaseOwner" = {ownerToken}
              AND "Status" = {(int)IdempotencyStatus.InProgress}
            """, ct);

    // Polls (bounded by deadline) for the row to reach Completed — used only
    // after THIS caller already lost its own publish race (see the steal-loss
    // branch above): it is purely watching for the authoritative attempt to
    // finish, never trying to reclaim the key itself.
    private static async Task<IdempotencyRecord?> WaitForCompletionAsync(
        AppDbContext db, Guid accountId, string endpointHash, string keyHash,
        DateTimeOffset deadline, CancellationToken ct)
    {
        while (true)
        {
            var existing = await db.IdempotencyRecords.AsNoTracking().FirstOrDefaultAsync(r =>
                r.AccountId == accountId && r.EndpointHash == endpointHash && r.IdempotencyKeyHash == keyHash, ct);
            if (existing is { Status: IdempotencyStatus.Completed }) return existing;
            // #307 review — the row can vanish out from under a waiter: the
            // authoritative claimant we're watching may itself fail (a non-2xx
            // response releases its claim via ReleaseClaimAsync). Nobody is
            // going to complete a claim that no longer exists, and reclaiming
            // it is not this function's job (that's the outer claim loop's
            // Retry handling) — so give up immediately instead of polling out
            // the rest of the bounded wait for nothing.
            if (existing is null) return null;
            if (DateTimeOffset.UtcNow >= deadline) return null;
            await Task.Delay(PollInterval, ct);
        }
    }

    private static Task ReplayAsync(HttpContext context, IdempotencyRecord record) =>
        EmitAsync(
            context,
            record.StatusCode ?? StatusCodes.Status200OK,
            record.ContentType,
            record.ResponseBody ?? string.Empty);

    // #340 — the single place this middleware puts a buffered or replayed
    // response back on the wire. It exists because there are THREE of them
    // (publish, non-2xx echo, replay) and they must not drift: each one has to
    // respect the statuses that carry no message body.
    //
    // Kestrel enforces that rule on ANY write once the status is set, and
    // HttpResponseWritingExtensions.WriteAsync("") is still a write — it calls
    // BodyWriter.GetSpan(...) then Advance(0), and HttpProtocol.Advance throws
    // "Writing to the response body is invalid for responses with status code
    // 204." So an empty echo is not a harmless no-op; before #307 the echo was
    // buffer.CopyToAsync(originalBody), which really did write nothing for an
    // empty buffer, and that is the only reason this never surfaced earlier.
    //
    // The failure is also invisible from the client side: WriteAsync starts the
    // response before it writes, so the 204 and its headers are already on the
    // wire when Advance throws — the caller sees a valid 204 while the server
    // logs an unhandled exception and the connection dies. Assert on the
    // server-side completion log, not the status code (see
    // KestrelResponseWriteTests).
    private static async Task EmitAsync(
        HttpContext context, int statusCode, string? contentType, string body)
    {
        context.Response.StatusCode = statusCode;
        if (!CanHaveBody(statusCode)) return;
        if (!string.IsNullOrWhiteSpace(contentType)) context.Response.ContentType = contentType;
        await context.Response.WriteAsync(body, context.RequestAborted);
    }

    // RFC 9110 §15.3.5 / §15.3.6 / §15.4.5 — 204, 205 and 304 responses never
    // carry a message body.
    private static bool CanHaveBody(int statusCode) => statusCode
        is not StatusCodes.Status204NoContent
        and not StatusCodes.Status205ResetContent
        and not StatusCodes.Status304NotModified;

    private static async Task WriteProblemAsync(HttpContext context, int status, string type, string title)
    {
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { type, title, status });
    }

    // Postgres unique_violation on the (AccountId, EndpointHash,
    // IdempotencyKeyHash) index — a concurrent fresh claim beat us to it.
    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation };

    // Hashes the raw request body (buffered + rewound so the real model
    // binder downstream still reads it). Bounded by whatever body-size cap
    // already applies ahead of this middleware (Kestrel's default, or the
    // #309 per-endpoint cap) — no additional limit needed here.
    private static async Task<string> ComputeRequestHashAsync(HttpRequest request, CancellationToken ct)
    {
        request.EnableBuffering();
        request.Body.Position = 0;
        using var ms = new MemoryStream();
        await request.Body.CopyToAsync(ms, ct);
        request.Body.Position = 0;
        return Convert.ToHexString(SHA256.HashData(ms.ToArray())).ToLowerInvariant();
    }

    private static string Sha256(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
