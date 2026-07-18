namespace Cluckwork.Infrastructure.Repositories;

using Cluckwork.Application.Features.Inventory;
using Cluckwork.Domain.Inventory;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed class InventoryMovementRepository(AppDbContext db) : IInventoryMovementRepository
{
    public Task<InventoryMovement?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.InventoryMovements.FirstOrDefaultAsync(m => m.Id == id, ct);

    public async Task<IReadOnlyList<InventoryMovement>> ListByItemAsync(
        Guid inventoryItemId, int limit, int offset, CancellationToken ct = default) =>
        await db.InventoryMovements
            .AsNoTracking()
            .Where(m => m.InventoryItemId == inventoryItemId)
            .OrderByDescending(m => m.Date).ThenByDescending(m => m.Id)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

    public async Task AddAsync(InventoryMovement entity, CancellationToken ct = default) =>
        await db.InventoryMovements.AddAsync(entity, ct);

    // Append-only ledger (spec §12.3): enforced here at the seam, matching
    // BirdMovementRepository.
    public void Update(InventoryMovement entity) =>
        throw new NotSupportedException("Inventory movements are append-only; record an Adjustment instead.");

    public void Remove(InventoryMovement entity) =>
        throw new NotSupportedException("Inventory movements are append-only; record an Adjustment instead.");
}
