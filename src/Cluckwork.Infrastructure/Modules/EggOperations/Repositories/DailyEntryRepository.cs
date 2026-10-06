using Cluckwork.Application.Modules.EggOperations.DailyEntries;
using Cluckwork.Domain.Modules.EggOperations.Contracts;
using Cluckwork.Domain.Modules.EggOperations.Eggs;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Modules.EggOperations.Repositories;

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

    // Write-side natural-key lookup (#388): same bypass as
    // GetByIdForFlockScopedWriteAsync above, reinstating AccountId explicitly
    // while keeping the full natural key and non-Voided predicate. Voided
    // entries are excluded because voiding vacates the natural key (#82); the
    // partial unique index (IX_DailyEntries_NaturalKey) allows one live match.
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
            .OrderByBusinessChronologyDescending(e => e.Date)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<DailyEntry>> ListSubmittedBeforeAsync(
        DateOnly before, int limit, CancellationToken ct = default) =>
        await db.DailyEntries
            .Where(e => e.Status == DailyEntryStatus.Submitted && e.Date < before)
            .OrderBy(e => e.Date)
            .Take(limit)
            .ToListAsync(ct);

    public async Task AddAsync(DailyEntry entity, CancellationToken ct = default) =>
        await db.DailyEntries.AddAsync(entity, ct);
}
