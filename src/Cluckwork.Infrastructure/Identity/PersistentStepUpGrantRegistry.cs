namespace Cluckwork.Infrastructure.Identity;

using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.SharedState;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #338 — the shared-store implementation of the two step-up-grant guarantees,
// replacing InMemoryStepUpGrantRegistry now that both tables have left the
// process:
//   - single-use (replay) -> IClaimOnceStore (#543): Redis when configured,
//     in-process on a single instance, and FAIL-CLOSED (claim denied) when Redis
//     is configured but unreachable — a privileged op is refused rather than
//     admitted without a replay proof.
//   - logout revocation    -> ApplicationUser.StepUpLogoutEpoch, a durable
//     per-user integer compared for equality (never Redis, never a timestamp).
//
// No lock, and no longer a singleton: neither table lives in this object, so it
// is a plain SCOPED service reaching the scoped AppDbContext directly. The
// atomicity the lock used to give is re-established by ORDERING — see
// TryConsumeIfNotLoggedOutAsync.
internal sealed class PersistentStepUpGrantRegistry(
    IClaimOnceStore claimOnce, AppDbContext db) : IStepUpGrantRegistry
{
    public async Task<bool> TryConsumeIfNotLoggedOutAsync(
        Guid userId, Guid jti, int grantEpoch, DateTimeOffset expiresAt,
        DateTimeOffset now, CancellationToken ct = default)
    {
        // A non-positive TTL is not a valid claim window; the caller already
        // treats an expired grant as a denial, so refuse without touching the store.
        var ttl = expiresAt - now;
        if (ttl <= TimeSpan.Zero) return false;

        // CONSUME FIRST, then read the logout epoch. This ordering is load-bearing
        // (#338) and replaces the single lock the in-memory registry held over
        // both tables — no lock can span Redis and Postgres:
        //
        //   * A logout incrementing the epoch BETWEEN the consume and the read is
        //     caught by the read below — the grant is refused.
        //   * A logout AFTER the read is genuinely after admission.
        //   * Reading the epoch BEFORE consuming reopens the #336 race: an
        //     increment in the gap would be missed by a validation already in
        //     flight. The order MUST NOT be flipped (a deterministic test pins it).
        //
        // A revoked grant still burns its one-time claim slot — harmless (refused
        // either way, and the claim self-expires) and INVISIBLE to the caller:
        // replay and logout-revocation both return a bare false, no distinct
        // error/latency/log (the non-enumerating contract).
        //
        // Fail-closed on Redis loss is inherited from ResilientClaimOnceStore.
        if (!claimOnce.TryClaim(jti.ToString(), ttl)) return false;

        var currentEpoch = await db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.StepUpLogoutEpoch)
            .FirstOrDefaultAsync(ct);

        // Equality, not >=: a grant can only ever carry an epoch <= the current
        // one (it read the value at issue), and any logout since issuance has
        // advanced it, so "still equal" is exactly "no logout since issued".
        return grantEpoch == currentEpoch;
    }

    public async Task RecordLogoutAsync(Guid userId, CancellationToken ct = default)
    {
        // Advance the epoch by one, in ONE statement. Monotonic — it only ever
        // increases, so it can never un-revoke a grant a prior logout already
        // killed, and concurrent increments serialize at the row lock.
        //
        // FENCE (#492 pattern; Codex #338 review). This is a bare ExecuteUpdate,
        // NOT a tracked SaveChanges, so it leaves ConcurrencyStamp untouched —
        // while Identity's UserStore.UpdateAsync issues a FULL-ENTITY update
        // guarded on that stamp. Without rotating it, a concurrent same-user
        // Identity write that read the row BEFORE this logout (login's
        // AccessFailedAsync / ResetAccessFailedCountAsync mid-request) would still
        // match the unrotated stamp and write its STALE StepUpLogoutEpoch back,
        // silently REVERTING the logout — a captured pre-logout grant would equal
        // the stored epoch again and work after logout. Rotating the stamp here
        // makes that stale full-entity write lose its CAS (0 rows / concurrency
        // failure), so the epoch bump stands. Pinned by
        // RecordLogout_FencesAStaleFullEntityWrite_SoTheEpochCannotBeReverted.
        //
        // RETRY NOTE (#269). This runs under EnableRetryOnFailure and is NOT
        // wrapped in SingleAttemptExecution — deliberately. An ambiguous-commit
        // replay re-issues the "+1" and bumps the epoch twice for one logout, but
        // unlike AccountLockout's counter (whose double-increment HARMS the user
        // via premature lockout) a double epoch bump only ever OVER-revokes
        // (refuses a grant issued in the narrow ambiguous window), never
        // under-revokes. The safe direction makes the extra increment harmless, so
        // the plain retryable statement is correct here.
        var rotatedStamp = Guid.NewGuid().ToString();
        await db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(u => u.StepUpLogoutEpoch, u => u.StepUpLogoutEpoch + 1)
                    .SetProperty(u => u.ConcurrencyStamp, rotatedStamp),
                ct);
    }

    public async Task<bool> IsRevokedByLogoutAsync(
        Guid userId, int grantEpoch, CancellationToken ct = default)
    {
        var currentEpoch = await db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.StepUpLogoutEpoch)
            .FirstOrDefaultAsync(ct);
        return grantEpoch != currentEpoch;
    }
}

// #338 — the concrete registry is internal (its constructor takes the internal
// IClaimOnceStore port), so the Api layer registers it through this extension
// rather than naming the type. Scoped, not singleton: it holds no in-process
// state now that replay lives in IClaimOnceStore and logout in Postgres.
public static class PersistentStepUpGrantRegistryRegistration
{
    public static IServiceCollection AddPersistentStepUpGrantRegistry(this IServiceCollection services)
    {
        services.AddScoped<IStepUpGrantRegistry, PersistentStepUpGrantRegistry>();
        return services;
    }
}
