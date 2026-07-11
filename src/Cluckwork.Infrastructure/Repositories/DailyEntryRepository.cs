namespace Cluckwork.Infrastructure.Repositories;

using Cluckwork.Application.Features.DailyEntries;
using Cluckwork.Domain.Eggs;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed class DailyEntryRepository(AppDbContext db) : IDailyEntryRepository
{
    public Task<DailyEntry?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.DailyEntries.FirstOrDefaultAsync(e => e.Id == id, ct);

    public Task<DailyEntry?> FindByNaturalKeyAsync(
        Guid accountId, Guid farmId, Guid houseId, Guid flockId, DateOnly date,
        CancellationToken ct = default) =>
        db.DailyEntries
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(e =>
                e.AccountId == accountId &&
                e.FarmId == farmId &&
                e.HouseId == houseId &&
                e.FlockId == flockId &&
                e.Date == date, ct);

    public async Task AddAsync(DailyEntry entity, CancellationToken ct = default) =>
        await db.DailyEntries.AddAsync(entity, ct);

    public void Update(DailyEntry entity) => db.DailyEntries.Update(entity);

    public void Remove(DailyEntry entity) => db.DailyEntries.Remove(entity);
}
