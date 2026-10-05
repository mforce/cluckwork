using Cluckwork.Application.Modules.EggOperations.Contracts;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Modules.EggOperations.Contracts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Repositories;

// Reads rely on the tenant query filter; ListSaleableGradeNamesAsync,
// AnyGradeAsync and the purge ignore it.
public sealed class EggOperationsFixture(AppDbContext db) : IEggOperationsFixture
{
    public async Task<IReadOnlyList<string>> ListSaleableGradeNamesAsync(
        Guid accountId, CancellationToken ct = default) =>
        await db.EggGrades
            .IgnoreQueryFilters()
            .Where(g => g.AccountId == accountId && g.IsSaleable)
            .Select(g => g.Name)
            .ToListAsync(ct);

    public Task<bool> DefaultHouseEntryExistsAsync(
        Guid accountId, Guid flockId, DateOnly date, CancellationToken ct = default) =>
        db.DailyEntries.AnyAsync(e =>
            e.AccountId == accountId &&
            e.FarmId == SeedDefaults.FarmId &&
            e.HouseId == SeedDefaults.HouseId &&
            e.FlockId == flockId &&
            e.Date == date &&
            e.Status != DailyEntryStatus.Voided, ct);

    public async Task<EggOperationsFixtureCounts> CountAsync(CancellationToken ct = default) =>
        new(
            DailyEntries: await db.DailyEntries.CountAsync(ct),
            DraftEntries: await db.DailyEntries.CountAsync(e => e.Status == DailyEntryStatus.Draft, ct),
            SubmittedEntries: await db.DailyEntries.CountAsync(e => e.Status == DailyEntryStatus.Submitted, ct),
            LockedEntries: await db.DailyEntries.CountAsync(e => e.Status == DailyEntryStatus.Locked, ct),
            EggLots: await db.EggLots.CountAsync(ct));

    public Task<bool> AnyGradeAsync(Guid accountId, CancellationToken ct = default) =>
        db.EggGrades
            .IgnoreQueryFilters()
            .AnyAsync(g => g.AccountId == accountId, ct);

    public async Task PurgeDailyEntriesAsync(Guid accountId, CancellationToken ct = default)
    {
        await db.EggInventoryMovements.IgnoreQueryFilters().Where(x => x.AccountId == accountId).ExecuteDeleteAsync(ct);
        await db.EggLots.IgnoreQueryFilters().Where(x => x.AccountId == accountId).ExecuteDeleteAsync(ct);
        await db.DailyEntryGrades.IgnoreQueryFilters().Where(x => x.AccountId == accountId).ExecuteDeleteAsync(ct);
        await db.DailyEntries.IgnoreQueryFilters().Where(x => x.AccountId == accountId).ExecuteDeleteAsync(ct);
    }
}
