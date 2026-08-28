namespace Cluckwork.Infrastructure.Repositories;

using Cluckwork.Application.Features.DailyEntries;
using Cluckwork.Domain.Eggs;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed class DailyEntryRepository(AppDbContext db) : IDailyEntryRepository
{
    // Grades are always eager-loaded: RecordProduction does a full replace of the
    // lines, which requires the current lines to be tracked (orphan delete).
    public Task<DailyEntry?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.DailyEntries
            .Include(e => e.Grades)
            .FirstOrDefaultAsync(e => e.Id == id, ct);

    public Task<DailyEntry?> GetByIdForFlockScopedWriteAsync(
        Guid id, Guid accountId, CancellationToken ct = default) =>
        db.DailyEntries
            .IgnoreQueryFilters()
            .Where(e => e.AccountId == accountId)
            .Include(e => e.Grades)
            .FirstOrDefaultAsync(e => e.Id == id, ct);

    // Voided entries are excluded: voiding vacates the natural key (#82), so
    // the day can be re-recorded as a fresh entry. The partial unique index
    // (IX_DailyEntries_NaturalKey) guarantees at most one live match.
    // The tenant query filter stays ON: the explicit AccountId predicate is
    // belt-and-suspenders, not the only guard (pi review of #83).
    public Task<DailyEntry?> FindByNaturalKeyAsync(
        Guid accountId, Guid farmId, Guid houseId, Guid flockId, DateOnly date,
        CancellationToken ct = default) =>
        db.DailyEntries
            .Include(e => e.Grades)
            .FirstOrDefaultAsync(e =>
                e.AccountId == accountId &&
                e.FarmId == farmId &&
                e.HouseId == houseId &&
                e.FlockId == flockId &&
                e.Date == date &&
                e.Status != DailyEntryStatus.Voided, ct);

    // Write-side natural-key lookup (#388): same bypass as
    // GetByIdForFlockScopedWriteAsync above, reinstating AccountId explicitly
    // while keeping the full natural key and non-Voided predicate.
    public Task<DailyEntry?> FindByNaturalKeyForFlockScopedWriteAsync(
        Guid accountId, Guid farmId, Guid houseId, Guid flockId, DateOnly date,
        CancellationToken ct = default) =>
        db.DailyEntries
            .IgnoreQueryFilters()
            .Include(e => e.Grades)
            .FirstOrDefaultAsync(e =>
                e.AccountId == accountId &&
                e.FarmId == farmId &&
                e.HouseId == houseId &&
                e.FlockId == flockId &&
                e.Date == date &&
                e.Status != DailyEntryStatus.Voided, ct);

    public Task<DailyEntry?> GetReadOnlyAsync(Guid id, CancellationToken ct = default) =>
        db.DailyEntries
            .AsNoTracking()
            .Include(e => e.Grades)
            .FirstOrDefaultAsync(e => e.Id == id, ct);

    public async Task<IReadOnlyList<DailyEntry>> ListAsync(
        Guid? flockId, DateOnly? from, DateOnly? to, int limit, int offset,
        CancellationToken ct = default) =>
        await db.DailyEntries
            .AsNoTracking()
            .Include(e => e.Grades)
            .Where(e => (flockId == null || e.FlockId == flockId)
                     && (from == null || e.Date >= from)
                     && (to == null || e.Date <= to))
            // Id tiebreaker: Date alone is non-unique, and unstable ordering under
            // OFFSET paging drops or duplicates rows across pages.
            .OrderByDescending(e => e.Date).ThenByDescending(e => e.Id)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

    public async Task AddAsync(DailyEntry entity, CancellationToken ct = default) =>
        await db.DailyEntries.AddAsync(entity, ct);

    public void Update(DailyEntry entity) => db.DailyEntries.Update(entity);

    public void Remove(DailyEntry entity) => db.DailyEntries.Remove(entity);
}
