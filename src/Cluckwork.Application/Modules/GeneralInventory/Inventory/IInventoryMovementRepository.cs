using Cluckwork.Domain.Modules.GeneralInventory.Inventory;

namespace Cluckwork.Application.Modules.GeneralInventory.Inventory;

// Append-only ledger, so the port has no Update or Remove: mistakes get
// compensating Adjustment rows, never edits.
public interface IInventoryMovementRepository
{
    Task AddAsync(InventoryMovement entity, CancellationToken ct = default);

    // Newest first (date, then id) — ledger browsing per item.
    Task<IReadOnlyList<InventoryMovement>> ListByItemAsync(
        Guid inventoryItemId, int limit, int offset, CancellationToken ct = default);
}
