using Cluckwork.Application.Modules.FlockManagement.Flocks;
using Cluckwork.Domain.Modules.FlockManagement.Flocks;
using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Modules.FlockManagement.Repositories;

public sealed class BirdMovementRepository(AppDbContext db) : IBirdMovementRepository
{
    public async Task<IReadOnlyList<BirdMovement>> ListByFlockAsync(
        Guid flockId, int limit, int offset, CancellationToken ct = default) =>
        await db.BirdMovements
            .AsNoTracking()
            .Where(m => m.FlockId == flockId)
            .OrderByBusinessChronologyDescending(m => m.Date)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

    // #512 T044 — bounded to the flocks the page actually returned. The old
    // signature aggregated the caller's ENTIRE visible movement ledger on every
    // flock list request, so the cost grew with the farm's all-time history
    // instead of with the page — the same defect #311 closed in the report path.
    // Empty ids means no aggregate query at all, not an unbounded one.
    public async Task<Dictionary<Guid, long>> RemovedForFlocksAsync(
        IReadOnlyCollection<Guid> flockIds, CancellationToken ct = default)
    {
        if (flockIds.Count == 0) return [];

        var ids = flockIds.Distinct().ToArray();
        return await db.BirdMovements
            .AsNoTracking()
            .Where(m => ids.Contains(m.FlockId))
            .GroupBy(m => m.FlockId)
            .Select(g => new { FlockId = g.Key, Removed = g.Sum(m => (long)m.Quantity) })
            .TagWith(ReferenceMarkers.MovementAggregate)
            .ToDictionaryAsync(x => x.FlockId, x => x.Removed, ct);
    }

    public Task<long> RemovedForFlockAsync(Guid flockId, CancellationToken ct = default) =>
        db.BirdMovements
            .AsNoTracking()
            .Where(m => m.FlockId == flockId)
            .SumAsync(m => (long)m.Quantity, ct);

    public async Task AddAsync(BirdMovement entity, CancellationToken ct = default) =>
        await db.BirdMovements.AddAsync(entity, ct);
}
