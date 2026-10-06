using Cluckwork.Application.Modules.GeneralInventory.Inventory;
using Cluckwork.Domain.Modules.GeneralInventory.Inventory;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Modules.GeneralInventory.Repositories;

public sealed class WaterUsageRepository(AppDbContext db) : IWaterUsageRepository
{
    public Task<WaterUsage?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.WaterUsages.FirstOrDefaultAsync(u => u.Id == id, ct);

    public async Task<IReadOnlyList<WaterUsage>> ListAsync(
        Guid? flockId, DateOnly? from, DateOnly? to,
        int limit, int offset, CancellationToken ct = default) =>
        await db.WaterUsages
            .AsNoTracking()
            .Where(u => (flockId == null || u.FlockId == flockId)
                     && (from == null || u.Date >= from)
                     && (to == null || u.Date <= to))
            .OrderByBusinessChronologyDescending(u => u.Date)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

    public async Task AddAsync(WaterUsage entity, CancellationToken ct = default) =>
        await db.WaterUsages.AddAsync(entity, ct);
}
