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
    private const int BatchSize = 200;

    public async Task RunAsync(CancellationToken ct)
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

        foreach (var (accountId, timeZoneId) in accounts)
        {
            try
            {
                await LockDueEntriesAsync(accountId, timeZoneId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One account's failure (bad timezone id, transient DB error)
                // must not starve the remaining accounts of their sweep.
                logger.LogError(ex, "Lock sweep failed for account {AccountId}.", accountId);
            }
        }
    }

    private async Task LockDueEntriesAsync(Guid accountId, string timeZoneId, CancellationToken ct)
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
        if (due.Count == 0) return;

        var lockedCount = 0;
        foreach (var entry in due)
        {
            var locked = entry.Lock(clock.UtcNow);
            if (locked.IsFailure)
                // Unreachable given the Submitted filter; loud if it ever isn't.
                logger.LogWarning("Could not lock entry {EntryId}: {Error}", entry.Id, locked.Error.Code);
            else
                lockedCount++;
        }

        // A concurrent adjust on one of these entries wins the Version token
        // race; the whole batch retries on the next poll minus that entry.
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Locked {Count} submitted entries older than {Days} days for account {AccountId}.",
            lockedCount, LockAfterDays, accountId);
    }
}
