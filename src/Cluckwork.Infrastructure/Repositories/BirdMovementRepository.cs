namespace Cluckwork.Infrastructure.Repositories;

using Cluckwork.Application.Features.Flocks;
using Cluckwork.Domain.Flocks;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed class BirdMovementRepository(AppDbContext db) : IBirdMovementRepository
{
    // Reads rely on the tenant query filter (AccountId == current tenant).
    public Task<BirdMovement?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.BirdMovements.FirstOrDefaultAsync(m => m.Id == id, ct);

    public async Task<IReadOnlyList<BirdMovement>> ListByFlockAsync(
        Guid flockId, int limit, int offset, CancellationToken ct = default) =>
        await db.BirdMovements
            .AsNoTracking()
            .Where(m => m.FlockId == flockId)
            .OrderByDescending(m => m.Date).ThenByDescending(m => m.Id)
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

    // The ledger is append-only: corrections are new Adjustment rows, never
    // edits or deletes — enforced here so no handler can drift.
    public void Update(BirdMovement entity) =>
        throw new NotSupportedException("Bird movements are append-only; record an Adjustment instead.");

    public void Remove(BirdMovement entity) =>
        throw new NotSupportedException("Bird movements are append-only; record an Adjustment instead.");
}
