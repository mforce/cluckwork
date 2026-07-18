namespace Cluckwork.Application.Features.Inventory;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Inventory;

// Append-only ledger: the infrastructure implementation throws
// NotSupportedException from Update/Remove (BirdMovement pattern) — mistakes
// get compensating Adjustment rows, never edits.
public interface IInventoryMovementRepository : IRepository<InventoryMovement, Guid>
{
    // Newest first (date, then id) — ledger browsing per item.
    Task<IReadOnlyList<InventoryMovement>> ListByItemAsync(
        Guid inventoryItemId, int limit, int offset, CancellationToken ct = default);
}
