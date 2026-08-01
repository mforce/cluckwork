namespace Cluckwork.Infrastructure.Jobs;

using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// #270 — refresh_tokens grows without bound: every login/refresh inserts a
// row and rotation keeps inserting, but nothing ever deletes an
// expired/revoked row. This sweep purges rows whose OWN ExpiresAt — the
// token's originally-promised lifetime, set once at mint time (see
// IdentityProvider.NewToken) and never touched by rotation — is older than
// PurgeGrace. Runs from the DurableJobWorker poll, alongside DailyEntryLockSweep.
//
// Deliberately keyed on ExpiresAt, NEVER on RevokedAt/CreatedAt. A rotated
// (RevokedAt-set) row keeps the ExpiresAt it was minted with, so a token
// rotated on day one of a 30-day life is retained for the remaining ~29 days —
// exactly as long as IdentityProvider.RefreshAsync's reuse-detection (the
// `stored.RevokedAt is not null` branch) can still catch a replay of it: that
// branch never itself consults ExpiresAt, so a revoked row is a live
// theft-detection tripwire for as long as it physically exists. Purging on
// time-since-revocation instead would shrink a stolen token's detectable-
// replay window to whatever the sweep's poll cadence allows — turning a
// delayed replay into a silent "unknown token" miss instead of a caught
// attack (see RefreshTokenPurgeSweepTests for the reproduction). Keying on
// ExpiresAt is the most conservative bound available: it never retires a row
// before the point a legitimately-issued, never-rotated sibling of the same
// age would already be rejected as expired anyway.
//
// PurgeGrace only pads that horizon — it never shrinks it. A day comfortably
// absorbs clock skew between app instances/DB and dwarfs the #176 idempotency
// grace (RefreshReuseGraceSeconds, seconds-scale) plus normal sweep-poll
// jitter, so a request landing right at the ExpiresAt boundary mid-sweep
// still completes normally.
//
// No tenant loop / TenantContext.Resolve, unlike DailyEntryLockSweep: RefreshToken
// carries AccountId but is deliberately NOT tenant-query-filtered (pre-auth
// lookups — see RefreshToken.cs, and AppDbContext.OnModelCreating has no
// HasQueryFilter for it). A single global delete on ExpiresAt can't bypass a
// filter that doesn't exist and prunes every account uniformly in one
// statement — the tenant-safe shape here IS the absence of scoping, not an
// explicit per-account resolve.
public sealed class RefreshTokenPurgeSweep(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<RefreshTokenPurgeSweep> logger)
{
    public static readonly TimeSpan PurgeGrace = TimeSpan.FromDays(1);

    public async Task RunAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var cutoff = timeProvider.GetUtcNow() - PurgeGrace;
        // Strictly OLDER than the cutoff: a row exactly at the boundary survives
        // to the next poll rather than racing the instant it crosses it.
        var deleted = await db.RefreshTokens
            .Where(t => t.ExpiresAt < cutoff)
            .ExecuteDeleteAsync(ct);

        if (deleted > 0)
            logger.LogInformation(
                "Purged {Count} refresh tokens expired before {Cutoff}.", deleted, cutoff);
    }
}
