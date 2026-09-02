namespace Cluckwork.Infrastructure.Jobs;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Eggs;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// #69 — spec §8.1 default: submitted entries lock automatically once they are
// older than 7 farm-local days, counted in the account's own timezone (the
// boundary every date rule now shares — #35/#155). Runs from the
// DurableJobWorker poll; idempotent — an entry is locked
// at most once (Submitted → Locked), and a concurrent admin adjust that wins
// the Version race simply leaves nothing for the sweep to lock.
public sealed class DailyEntryLockSweep(
    IServiceScopeFactory scopeFactory,
    ILogger<DailyEntryLockSweep> logger)
{
    public const int LockAfterDays = 7;

    // Public because #638's caller has to reason about it: one RunAsync pass
    // locks at most this many entries PER ACCOUNT and returns, by design — the
    // DurableJobWorker poll picks the rest up 30s later. A caller with no poll
    // behind it (SimulationDataSeeder) must drain instead, and derives its own
    // pass bound from this number rather than guessing one.
    public const int BatchSize = 200;

    /// <summary>
    /// Runs one lock pass over every account.
    /// </summary>
    /// <returns>
    /// How many entries this pass actually locked, across all accounts — at
    /// most <see cref="BatchSize"/> per account. A non-zero return means there
    /// may be more still due: re-invoke until it returns 0 to drain the
    /// backlog. Zero means either nothing was due or nothing could be locked;
    /// either way there is no progress left for a re-invoke to make, so it is
    /// a safe loop terminator and cannot spin on a stuck batch.
    /// </returns>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        // Accounts first (filter-free), then one tenant-resolved scope per
        // account so the query filter and stamp interceptor behave exactly as
        // in a request.
        List<(Guid Id, string TimeZoneId)> accounts;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            accounts = (await db.Accounts
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Select(a => new { a.Id, a.TimeZoneId })
                .ToListAsync(ct))
                .Select(a => (a.Id, a.TimeZoneId))
                .ToList();
        }

        var lockedTotal = 0;
        foreach (var (accountId, timeZoneId) in accounts)
        {
            try
            {
                lockedTotal += await LockDueEntriesAsync(accountId, timeZoneId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One account's failure (bad timezone id, transient DB error)
                // must not starve the remaining accounts of their sweep. It
                // contributes 0 to the total, so a draining caller stops
                // re-invoking on an account that can never make progress
                // instead of looping on it.
                logger.LogError(ex, "Lock sweep failed for account {AccountId}.", accountId);
            }
        }

        return lockedTotal;
    }

    private async Task<int> LockDueEntriesAsync(Guid accountId, string timeZoneId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var tenant = scope.ServiceProvider.GetRequiredService<TenantContext>();
        tenant.Resolve(accountId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        // Strictly OLDER than 7 days: an entry exactly 7 days old keeps its
        // final editable day (codex review of PR #80).
        var cutoff = clock.TodayInZone(timeZoneId).AddDays(-LockAfterDays);
        var due = await db.DailyEntries
            .Where(e => e.Status == DailyEntryStatus.Submitted && e.Date < cutoff)
            .OrderBy(e => e.Date)
            .Take(BatchSize)
            .ToListAsync(ct);
        if (due.Count == 0) return 0;

        var lockedCount = 0;
        foreach (var entry in due)
        {
            var locked = entry.Lock(clock.UtcNow);
            if (locked.IsFailure)
                // Unreachable given the Submitted filter; loud if it ever
                // isn't. Property names follow the #216 canonical failure
                // shape so one query spans handlers and jobs.
                logger.LogWarning(
                    "LockDailyEntry failed for entry {DailyEntryId}: {ErrorCode} — {ErrorDescription}",
                    entry.Id, locked.Error.Code, locked.Error.Description);
            else
                lockedCount++;
        }

        // A concurrent adjust on one of these entries wins the Version token
        // race; the whole batch retries on the next poll minus that entry.
        await db.SaveChangesAsync(ct);
        // Per-entry AFTER the save (#216 AC: lock is a state transition too;
        // logging before commit would narrate locks that never happened).
        // Background job — no request scope, so AccountId rides explicitly.
        foreach (var entry in due.Where(e => e.Status == DailyEntryStatus.Locked))
            logger.LogInformation(
                "Daily entry {DailyEntryId} locked for flock {FlockId} on {EntryDate} (account {AccountId})",
                entry.Id, entry.FlockId, entry.Date, accountId);
        logger.LogInformation(
            "Locked {Count} submitted entries older than {Days} days for account {AccountId}.",
            lockedCount, LockAfterDays, accountId);
        // Deliberately the count that was actually LOCKED, not due.Count: a
        // batch that came back due but locked nothing (every Lock() failed, or
        // a concurrent adjust won every Version race) has made no progress, and
        // reporting it as progress would let a draining caller spin forever on
        // the same batch.
        return lockedCount;
    }
}
