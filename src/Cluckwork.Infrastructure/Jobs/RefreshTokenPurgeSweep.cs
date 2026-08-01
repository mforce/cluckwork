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

    // Bounded like DailyEntryLockSweep's BatchSize, and for the same reason. The
    // premise of #270 is a table that has accumulated for months, so the FIRST
    // sweep after this ships is the big one: an unbounded ExecuteDelete would take
    // it in a single statement — long lock retention, a WAL spike, and a mound of
    // dead tuples for autovacuum. Drain in batches instead, capped per tick so one
    // poll can't monopolise the worker; whatever is left goes on the next tick.
    private const int BatchSize = 500;
    private const int MaxBatchesPerRun = 20;

    // So a test can seed a backlog that provably spans more than one batch
    // without hardcoding the size in two places.
    internal static int BatchSizeForTests => BatchSize;

    public async Task RunAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var cutoff = timeProvider.GetUtcNow() - PurgeGrace;
        var total = 0;
        var capped = true;

        try
        {
            for (var batch = 0; batch < MaxBatchesPerRun; batch++)
            {
                // Strictly older than the cutoff. Exact equality is a measure-zero
                // case against a moving clock and carries no guarantee worth
                // pinning — what matters, and what the tests pin, is the retention
                // WINDOW: a row is safe until its own ExpiresAt plus the grace.
                var deleted = await db.RefreshTokens
                    .Where(t => t.ExpiresAt < cutoff)
                    .OrderBy(t => t.ExpiresAt)
                    .Take(BatchSize)
                    .ExecuteDeleteAsync(ct);

                total += deleted;
                if (deleted < BatchSize)
                {
                    capped = false;
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Contain the failure the way DailyEntryLockSweep does. This is
            // housekeeping: letting a transient DB error escape would fail the
            // whole poll iteration and push the worker into backoff, delaying the
            // business-critical daily-entry lock sweep along with it.
            logger.LogError(ex, "Refresh-token purge sweep failed; will retry next poll.");
            return;
        }

        if (total > 0)
            logger.LogInformation(
                "Purged {Count} refresh tokens expired before {Cutoff}.", total, cutoff);

        if (capped)
            logger.LogInformation(
                "Refresh-token purge hit its {Max}-batch cap; more remain for the next poll.",
                MaxBatchesPerRun);
    }
}
