namespace Cluckwork.Infrastructure.Jobs;

using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// #259 — idempotency_records grows without bound: IdempotencyMiddleware inserts
// a claim per idempotent write and marks it Completed with the replay payload,
// but nothing ever deletes an old row. This sweep purges rows whose CreatedAt is
// older than PurgeRetention. Runs from the DurableJobWorker poll, alongside
// DailyEntryLockSweep and RefreshTokenPurgeSweep.
//
// Keyed on CreatedAt, and PurgeRetention is a single flat window applied to
// EVERY row regardless of Status. Both choices are deliberate:
//
//   * CreatedAt, not CompletedAt — CompletedAt is null on an InProgress row
//     (#307), so it cannot bound both states; CreatedAt is stamped once at claim
//     insert and is present on every row. A row's only post-hoc value is
//     replaying its Completed response to a client retry, and PurgeRetention is
//     chosen far beyond any plausible retry horizon (48h vs. the seconds-to-
//     minutes a client actually retries), so age-since-creation is the right and
//     sufficient bound. There is no theft-tripwire subtlety here as there is for
//     RefreshTokenPurgeSweep's ExpiresAt keying — an idempotency row protects a
//     retry, not a revocation, so nothing needs it to outlive its creation age.
//
//   * All statuses, including InProgress — a lease lives LeaseDurationSeconds
//     (IdempotencyOptions, 30s by default), so any InProgress row 48h old is an
//     abandoned claim whose holder crashed long ago; the atomic protocol already
//     treats an expired lease as stealable, and deleting the row instead simply
//     lets the next same-key request INSERT a fresh claim. A live in-flight
//     request can never be caught by this window.
//
// No tenant loop / TenantContext.Resolve, like RefreshTokenPurgeSweep and unlike
// DailyEntryLockSweep: IdempotencyRecord carries AccountId but is deliberately
// NOT tenant-query-filtered (the middleware runs before tenant resolution and
// keys claims by (AccountId, EndpointHash, IdempotencyKeyHash) itself — see
// AppDbContext.OnModelCreating, which has no HasQueryFilter for it). A single
// global delete on CreatedAt can't bypass a filter that doesn't exist and prunes
// every account uniformly in one statement — the tenant-safe shape here IS the
// absence of scoping.
//
// The window is a compile-time constant rather than config, matching
// RefreshTokenPurgeSweep.PurgeGrace (the sibling #270 sweep) and DailyEntryLock-
// Sweep.LockAfterDays: Infrastructure does not reference the Api project where
// IdempotencyOptions lives, and 48h needs no per-deploy tuning — it only has to
// clear the client retry horizon, which it does by orders of magnitude.
public sealed class IdempotencyRecordPurgeSweep(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<IdempotencyRecordPurgeSweep> logger)
{
    // Comfortably beyond any client retry horizon (the point at which replaying a
    // cached response could still matter), so a row is only ever collected once
    // no legitimate retry of its request could still arrive.
    public static readonly TimeSpan PurgeRetention = TimeSpan.FromHours(48);

    // Bounded like RefreshTokenPurgeSweep's BatchSize, and for the same reason.
    // The premise of #259 is a table that has accumulated for months, so the
    // FIRST sweep after this ships is the big one: an unbounded ExecuteDelete
    // would take it in a single statement — long lock retention, a WAL spike, and
    // a mound of dead tuples for autovacuum. Drain in batches instead, capped per
    // tick so one poll can't monopolise the worker; whatever is left goes on the
    // next tick.
    private const int BatchSize = 500;
    private const int MaxBatchesPerRun = 20;

    // So a test can seed a backlog that provably spans more than one batch
    // without hardcoding the size in two places.
    internal static int BatchSizeForTests => BatchSize;

    public async Task RunAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var cutoff = timeProvider.GetUtcNow() - PurgeRetention;
        var total = 0;
        var capped = true;

        try
        {
            for (var batch = 0; batch < MaxBatchesPerRun; batch++)
            {
                // Strictly older than the cutoff. Exact equality is a measure-zero
                // case against a moving clock; what matters, and what the tests
                // pin, is the retention WINDOW: a row is safe until its own
                // CreatedAt plus the retention.
                var deleted = await db.IdempotencyRecords
                    .Where(r => r.CreatedAt < cutoff)
                    .OrderBy(r => r.CreatedAt)
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
            // Contain the failure the way DailyEntryLockSweep and
            // RefreshTokenPurgeSweep do. This is housekeeping: letting a transient
            // DB error escape would fail the whole poll iteration and push the
            // worker into backoff, delaying the business-critical daily-entry lock
            // sweep along with it.
            logger.LogError(ex, "Idempotency-record purge sweep failed; will retry next poll.");
            return;
        }

        if (total > 0)
            logger.LogInformation(
                "Purged {Count} idempotency records created before {Cutoff}.", total, cutoff);

        if (capped)
            logger.LogInformation(
                "Idempotency-record purge hit its {Max}-batch cap; more remain for the next poll.",
                MaxBatchesPerRun);
    }
}
