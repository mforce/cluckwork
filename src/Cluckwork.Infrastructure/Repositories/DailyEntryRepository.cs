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

    public Task<DailyEntry?> FindByNaturalKeyAsync(
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
                e.Date == date, ct);

    public async Task<IReadOnlyList<DailyEntry>> ListAsync(
        Guid? flockId, DateOnly? from, DateOnly? to, int limit, int offset,
        CancellationToken ct = default) =>
        await db.DailyEntries
            .AsNoTracking()
            .Include(e => e.Grades)
            .Where(e => (flockId == null || e.FlockId == flockId)
                     && (from == null || e.Date >= from)
                     && (to == null || e.Date <= to))
            .OrderByDescending(e => e.Date)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

    public async Task AddAsync(DailyEntry entity, CancellationToken ct = default) =>
        await db.DailyEntries.AddAsync(entity, ct);

    public void Update(DailyEntry entity) => db.DailyEntries.Update(entity);

    public void Remove(DailyEntry entity) => db.DailyEntries.Remove(entity);
}
