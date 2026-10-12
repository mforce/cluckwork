using System.Security.Cryptography;
using Cluckwork.Application.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

public sealed partial class IdentityProvider
{
    private RefreshToken NewToken(ApplicationUser user, string tokenHash)
    {
        var now = timeProvider.GetUtcNow();
        return new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            AccountId = user.AccountId,
            TokenHash = tokenHash,
            IssuedEpoch = user.CredentialEpoch,
            CreatedAt = now,
            ExpiresAt = now.AddDays(jwtOptions.Value.RefreshTokenDays)
        };
    }

    // Every caller bumps CredentialEpoch in the same transaction. The bump already refuses
    // the user's connected apps; revoking their authorizations and tokens too is what takes
    // them off Connected apps and lets OAuthPurge prune them (#1229). A new ConcurrencyToken
    // fails any in-flight OpenIddict write of a row, which would otherwise put `valid` back.
    private async Task RevokeAllActiveForUserAsync(
        Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        await RevokeAllActiveForUserAsync(userId, issuedEpoch: null, now, ct);
        var subject = userId.ToString();
        var concurrencyToken = Guid.NewGuid().ToString();
        await RunRevocationAsync(userId, async () =>
        {
            await db.OAuthAuthorizations
                .Where(authorization => authorization.Subject == subject
                    && authorization.Status == OpenIddictConstants.Statuses.Valid)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(authorization => authorization.Status, OpenIddictConstants.Statuses.Revoked)
                    .SetProperty(authorization => authorization.ConcurrencyToken, concurrencyToken), ct);
            await db.OAuthTokens
                .Where(row => row.Subject == subject && (row.Status == OpenIddictConstants.Statuses.Valid
                    || row.Status == OpenIddictConstants.Statuses.Inactive))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.Status, OpenIddictConstants.Statuses.Revoked)
                    .SetProperty(row => row.ConcurrencyToken, concurrencyToken), ct);
        });
    }

    private async Task RevokeAllActiveForUserAsync(
        Guid userId, int? issuedEpoch, DateTimeOffset now, CancellationToken ct)
    {
        // Bulk update rather than tracked read-modify-save: it never trips the
        // #176 xmin concurrency token (so it is safe to call from the rotation
        // fail path) and revokes the whole family in one statement.
        // Merge note: #273's RunRevocationAsync wrapper and #364's epoch scoping
        // are independent and BOTH apply. The wrapper only adds an alertable
        // event when the update throws; the filter decides which rows it
        // touches. Losing either half is silent — without the wrapper a failed
        // lock-out goes unreported, and without the filter a revoke reaches a
        // newer epoch's sessions.
        await RunRevocationAsync(
            userId,
            () => db.RefreshTokens
                .Where(t => t.UserId == userId && t.RevokedAt == null
                    && (!issuedEpoch.HasValue || t.IssuedEpoch == issuedEpoch.Value))
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct));
    }

    // #273 — hardening point for the BULK-UPDATE refresh-token revocations
    // this class performs (replay-triggered family revocation, logout,
    // password reset/change, break-glass — via RevokeAllActiveForUserAsync and
    // RevokeRefreshTokenAsync): if the bulk update itself throws instead of
    // completing, the safety action meant to lock a suspected attacker out of
    // every session never ran. That is worth its own alertable event
    // (Auth.RefreshRevocationFailed), separate from whatever triggered the
    // revoke attempt (a replay, a logout, a password change, ...) — logging it
    // here once, rather than at each bulk-update call site, is what keeps it
    // from being forgotten at a future one. Logs and RETHROWS: the caller's
    // existing behavior on a DB failure (bubble up, eventually a 500) is
    // unchanged — this only adds observability, never swallows the failure.
    // OperationCanceledException is excluded: a client hangup/cancellation
    // aborting the update is not a security signal worth alerting on.
    //
    // #273 codex review (P2e) — NOT the only revocation this class performs:
    // RefreshAsync's own token rotation also revokes (a tracked
    // read-modify-save, not a bulk update — see its own try/catch), and that
    // path cannot funnel through this method's Func<Task> shape without also
    // swallowing the DbUpdateConcurrencyException it must handle differently
    // (a benign race, not a failure worth alerting on). Both paths log through
    // the same LogRevocationFailed helper below, so they stay identical in
    // shape even though they can't share this wrapper's control flow.
    //
    // #273 codex review (round 2, P2c) — the boundary takes the user id as an
    // ACCESSOR, not a value, because RevokeRefreshTokenAsync's boundary now also
    // covers the lookup that discovers who the token belongs to: at the moment
    // the boundary is entered there is no user id yet, and at the moment it
    // catches there may or may not be one. The Guid?-taking overload below keeps
    // the three call sites that do know it up front unchanged.
    private async Task RunRevocationAsync(Func<Guid?> userId, Func<Task> revoke)
    {
        try
        {
            await revoke();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRevocationFailed(ex, userId());
            throw;
        }
    }

    private Task RunRevocationAsync(Guid? userId, Func<Task> revoke) =>
        RunRevocationAsync(() => userId, revoke);

    // #273 codex review (P2e) — the actual log call, factored out so
    // RunRevocationAsync's three bulk-update revocations (family revoke,
    // logout, break-glass/password-reset/change's RevokeAllActiveForUserAsync)
    // and RefreshAsync's tracked rotation (which revokes by read-modify-save,
    // not a bulk update, so it cannot go through RunRevocationAsync's
    // Func<Task> shape without also swallowing the DbUpdateConcurrencyException
    // it needs to handle separately) log the IDENTICAL event/shape rather than
    // risking the two call sites drifting apart.
    private void LogRevocationFailed(Exception ex, Guid? userId) =>
        logger.LogError(ex, "{SecurityEvent} user={UserId}",
            SecurityEvents.RefreshRevocationFailed, userId);

    // #269 — "did the save I just attempted actually commit?", answered by the
    // one piece of evidence that cannot belong to anybody else: the hash of a
    // 256-bit value THIS attempt generated moments ago and has not yet handed to
    // a single caller. No other request, and no competing consumer of the same
    // refresh token, can have written this row — they mint their own random
    // token. So its presence is proof the attempt's transaction became durable,
    // and its absence is proof nothing of ours did (EF's save is one transaction:
    // the rotation UPDATE and the INSERT land together or not at all).
    //
    // Fails CLOSED by construction: the caller rethrows unless this returns true,
    // so a probe that cannot reach the database reports the original error and
    // the client retries — never a session invented from a failed read.
    private Task<bool> MintedTokenIsDurableAsync(string mintedHash) =>
        // AnyAsync compiles to SELECT EXISTS (...) — a scalar. It therefore reads
        // the DATABASE and can never be answered from the change tracker's own
        // still-Added copy of the very row it is asking about, which a
        // FirstOrDefaultAsync could be via identity resolution.
        //
        // CancellationToken.None deliberately: a client that has already
        // disconnected must not leave a live token stranded, and this call is
        // what decides whether one was minted. Same reasoning as
        // IdempotencyMiddleware's post-failure claim probe.
        db.RefreshTokens.AnyAsync(t => t.TokenHash == mintedHash, CancellationToken.None);

    // The save threw, so EF still tracks these as Added/Modified even though the
    // database has already accepted them. Detach exactly the entities this call
    // touched, so any later SaveChanges cannot re-flush an already-durable row
    // (a duplicate key or a phantom concurrency conflict, attributed to us).
    //
    // Per-entity, never db.ChangeTracker.Clear(): on the owned/CLI path this same
    // AppDbContext can be shared by a longer-lived caller holding its own pending
    // writes, and clearing drops them silently (the regression that broke
    // SimulationDataSeeder — see SingleAttemptExecution). This is the same
    // surgical detach IdempotencyMiddleware.TryClaimOrInspectAsync uses.
    private void DetachCommitted(params object[] entities)
    {
        foreach (var entity in entities)
            db.Entry(entity).State = EntityState.Detached;
    }

    // 256-bit random token; only the SHA-256 hash is persisted.
    private static (string Raw, string Hash) GenerateRefreshToken()
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return (raw, Hash(raw));
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
