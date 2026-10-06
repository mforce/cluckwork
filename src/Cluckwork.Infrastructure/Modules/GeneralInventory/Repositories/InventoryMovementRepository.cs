using Cluckwork.Application.Modules.GeneralInventory.Inventory;
using Cluckwork.Domain.Modules.GeneralInventory.Inventory;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Modules.GeneralInventory.Repositories;

public sealed class InventoryMovementRepository(AppDbContext db) : IInventoryMovementRepository
{
    public async Task<IReadOnlyList<InventoryMovement>> ListByItemAsync(
        Guid inventoryItemId, int limit, int offset, CancellationToken ct = default) =>
        await db.InventoryMovements
            .AsNoTracking()
            .Where(m => m.InventoryItemId == inventoryItemId)
            .OrderByBusinessChronologyDescending(m => m.Date)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

    public async Task AddAsync(InventoryMovement entity, CancellationToken ct = default) =>
        await db.InventoryMovements.AddAsync(entity, ct);
}
