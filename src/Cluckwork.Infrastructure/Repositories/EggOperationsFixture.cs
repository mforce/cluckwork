using Cluckwork.Application.Features.Eggs;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Eggs;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Repositories;

// Reads other than ListSaleableGradeNamesAsync rely on the tenant query filter.
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
}
