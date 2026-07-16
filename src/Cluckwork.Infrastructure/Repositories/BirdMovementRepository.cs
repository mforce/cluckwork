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

    public async Task<Dictionary<Guid, long>> RemovedByFlockAsync(CancellationToken ct = default) =>
        await db.BirdMovements
            .AsNoTracking()
            .GroupBy(m => m.FlockId)
            .Select(g => new { FlockId = g.Key, Removed = g.Sum(m => (long)m.Quantity) })
            .ToDictionaryAsync(x => x.FlockId, x => x.Removed, ct);

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
