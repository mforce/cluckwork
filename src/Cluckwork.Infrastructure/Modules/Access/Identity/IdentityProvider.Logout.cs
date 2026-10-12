using Cluckwork.Application.Modules.Access.Contracts;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cluckwork.Infrastructure.Modules.Access.Identity;

public sealed partial class IdentityProvider
{
    public async Task<RefreshTokenRevocationOutcome> RevokeRefreshTokenAsync(
        string refreshToken, CancellationToken ct = default, Guid? expectedAccountId = null)
    {
        var presentedHash = Hash(refreshToken);
        var now = timeProvider.GetUtcNow();
        Guid? ownerId = null;
        var outcome = RefreshTokenRevocationOutcome.OutOfScope;

        // #273 codex review (round 2, P2c) — the failure boundary covers the
        // owner LOOKUP as well as the update. The lookup is a prerequisite of
        // the revocation, not something happening beside it: if it throws, the
        // lineage command below never runs, so the revocation fails just as
        // completely as if the command had thrown — but it used to do so in
        // silence, with no Auth.RefreshRevocationFailed for a deployment to
        // alert on. One boundary over the whole sequence, and EXACTLY ONE
        // emission either way: the CTE inside is raw SQL, never a second
        // RunRevocationAsync wrapper, so a failing command cannot log the event
        // twice.
        //
        // `ownerId` is read through a closure at catch time rather than passed
        // in, because who the token belongs to is only known once the lookup
        // this boundary now covers has succeeded — a lookup failure logs the
        // event with no user id, which is honest, rather than not at all.
        await RunRevocationAsync(() => ownerId, async () =>
        {
            // #308 — a real logout must invalidate any outstanding step-up grant
            // for this user (a grant captured before this logout must not work
            // after it), so read the owning user id BEFORE the lineage command.
            //
            // Deliberately NOT filtered on RevokedAt: the cookie may already be
            // stale because a background refresh rotated it moments earlier, and
            // #336's review caught that the revoked-only lookup silently skipped
            // recording the logout in exactly that case — leaving a grant valid
            // for the rest of its lifetime after the user had logged out.
            // Identifying the user is what matters here, not whether this
            // particular row is still live. A genuinely unknown token still
            // records nothing (logout is best-effort and always fires, see
            // AuthEndpoints.Logout).
            var tokenRow = await db.RefreshTokens
                .Where(t => t.TokenHash == presentedHash)
                .Select(t => new { t.UserId, t.AccountId, t.IssuedEpoch })
                .FirstOrDefaultAsync(ct);

            // A selected-farm logout may also carry the temporary shared-name
            // legacy cookie for another farm. Resolve its durable owner before
            // either revocation axis and refuse to cross that account boundary.
            // An unknown/purged token likewise produces no write: there is no
            // owner we can safely attribute, and an unknown token has no live
            // row to revoke.
            if (expectedAccountId is { } expectedAccount
                && tokenRow?.AccountId != expectedAccount)
                return;

            ownerId = tokenRow?.UserId;
            if (tokenRow is not null)
                outcome = RefreshTokenRevocationOutcome.InScope;

            // #336 review (2nd round) — record BEFORE token revocation, not after.
            // Since #338 RecordLogoutAsync advances the durable StepUpLogoutEpoch
            // in Postgres, so it CAN throw just like ExecuteUpdateAsync (transient
            // connection loss, deadlock) — it is no longer the infallible in-memory
            // record it once was. The ordering still holds: when the epoch bump
            // SUCCEEDS and the lineage command below throws, the grant-kill has already
            // committed, so the failure over-revokes (grants dead, refresh row
            // possibly still live) rather than under-revoking. When the bump itself
            // throws, the whole revocation fails loudly (both hit the same
            // database) and the short-lived grant lapses on its own expiry while
            // the SPA has already cleared its local token — the same fail-safe
            // direction AuthEndpoints.Logout applies to the bearer path one layer
            // up. On a transient blip EnableRetryOnFailure replays the bump; a
            // double increment only over-revokes, so the replay is safe (see the
            // retry note on PersistentStepUpGrantRegistry.RecordLogoutAsync).
            if (tokenRow is not null)
                await stepUpGrants.RecordLogoutAsync(tokenRow.UserId, ct);

            if (tokenRow is not null)
            {
                await RevokeExactLineageCoreAsync(
                    presentedHash,
                    tokenRow.UserId,
                    tokenRow.AccountId,
                    tokenRow.IssuedEpoch,
                    now,
                    ct);
            }
        });

        return outcome;
    }

    // #336 review — the access-token half of logout revocation. The cookie owner
    // above and the caller's authenticated identity can be DIFFERENT users (see
    // IIdentityProvider.RecordLogoutAsync), so the cookie alone cannot identify
    // who logged out. Advances the user's StepUpLogoutEpoch; refresh tokens are
    // untouched.
    public async Task RecordLogoutAsync(Guid userId, CancellationToken ct = default)
    {
        await stepUpGrants.RecordLogoutAsync(userId, ct);
    }

    // Chases only the durable lineage named by the presented row. One
    // PostgreSQL data-modifying CTE fences each still-live tip with a
    // `RevokedAt IS NULL` predicate and rotates its ConcurrencyStamp. Only when
    // that fence returns a row does it sever every visited ancestor pointer in
    // the same statement.
    // If refresh publishes a child first, scalar false rereads this hash and
    // follows that child; if logout updates first, refresh's tracked save loses
    // its stale stamp and its child insert rolls back.
    //
    // Raw CTE statements stay inside RevokeRefreshTokenAsync's one
    // RunRevocationAsync boundary. Each read/CTE is a self-contained EF unit,
    // so transient replay is safe without an explicit transaction or a new
    // SingleAttemptExecution region (#269).
    private async Task RevokeExactLineageCoreAsync(
        string presentedHash,
        Guid rootUserId,
        Guid rootAccountId,
        int rootIssuedEpoch,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var visitedHashes = new HashSet<string>(StringComparer.Ordinal);
        var currentHash = presentedHash;
        while (true)
        {
            if (!visitedHashes.Add(currentHash))
            {
                throw new InvalidOperationException(
                    "Refresh-token replacement lineage contains a cycle.");
            }

            while (true)
            {
                var node = await db.RefreshTokens
                    .AsNoTracking()
                    .Where(token => token.TokenHash == currentHash
                        && token.UserId == rootUserId
                        && token.AccountId == rootAccountId
                        && token.IssuedEpoch == rootIssuedEpoch)
                    .Select(token => new
                    {
                        token.RevokedAt,
                        token.ReplacedByTokenHash,
                    })
                    .SingleOrDefaultAsync(ct);

                if (node is null)
                {
                    throw new InvalidOperationException(
                        "Refresh-token replacement lineage left its durable owner scope.");
                }

                if (node.RevokedAt is not null)
                {
                    if (node.ReplacedByTokenHash is null)
                    {
                        return;
                    }
                    currentHash = node.ReplacedByTokenHash;
                    break;
                }

                var rotatedStamp = Guid.NewGuid().ToString();
                var ancestorHashes = visitedHashes
                    .Where(hash => !string.Equals(hash, currentHash, StringComparison.Ordinal))
                    .ToArray();
                var tipFenced = await ExecuteLineageFenceAsync(
                    currentHash,
                    ancestorHashes,
                    rootUserId,
                    rootAccountId,
                    rootIssuedEpoch,
                    now,
                    rotatedStamp,
                    ct);

                if (tipFenced)
                    return;

                // Zero means refresh won this row race and may have published a
                // child. Reread the same hash before deciding where the tip is.
            }
        }
    }

    private async Task<bool> ExecuteLineageFenceAsync(
        string currentHash,
        string[] ancestorHashes,
        Guid rootUserId,
        Guid rootAccountId,
        int rootIssuedEpoch,
        DateTimeOffset now,
        string rotatedStamp,
        CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            const string sql = """
                    WITH fenced_tip AS (
                        UPDATE refresh_tokens
                        SET "RevokedAt" = @revokedAt,
                            "ConcurrencyStamp" = @rotatedStamp,
                            "ReplacedByTokenHash" = NULL
                        WHERE "TokenHash" = @currentHash
                          AND "UserId" = @rootUserId
                          AND "AccountId" = @rootAccountId
                          AND "IssuedEpoch" = @rootIssuedEpoch
                          AND "RevokedAt" IS NULL
                        RETURNING 1
                    ),
                    severed_ancestors AS (
                        UPDATE refresh_tokens AS ancestor
                        SET "ReplacedByTokenHash" = NULL,
                            "ConcurrencyStamp" = @rotatedStamp
                        WHERE ancestor."TokenHash" = ANY (@ancestorHashes)
                          AND ancestor."UserId" = @rootUserId
                          AND ancestor."AccountId" = @rootAccountId
                          AND ancestor."IssuedEpoch" = @rootIssuedEpoch
                          AND ancestor."ReplacedByTokenHash" IS NOT NULL
                          AND EXISTS (SELECT 1 FROM fenced_tip)
                        RETURNING 1
                    )
                    SELECT EXISTS (SELECT 1 FROM fenced_tip) AS "Value"
                    FROM (SELECT count(*) FROM severed_ancestors) AS severed
                    """;
            var rawCommand = db.GetService<IRawSqlCommandBuilder>().Build(sql,
            [
                new NpgsqlParameter("revokedAt", now),
                new NpgsqlParameter("rotatedStamp", rotatedStamp),
                new NpgsqlParameter("currentHash", currentHash),
                new NpgsqlParameter("rootUserId", rootUserId),
                new NpgsqlParameter("rootAccountId", rootAccountId),
                new NpgsqlParameter("rootIssuedEpoch", rootIssuedEpoch),
                new NpgsqlParameter<string[]>("ancestorHashes", ancestorHashes),
            ], db.Model);
            var parameterObject = new RelationalCommandParameterObject(
                db.GetService<IRelationalConnection>(),
                rawCommand.ParameterValues,
                readerColumns: null,
                db,
                db.GetService<IRelationalCommandDiagnosticsLogger>(),
                CommandSource.ExecuteSqlRaw);
            return await rawCommand.RelationalCommand.ExecuteScalarAsync(parameterObject, ct) is true;
        });
    }
}
