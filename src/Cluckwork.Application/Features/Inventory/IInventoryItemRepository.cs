namespace Cluckwork.Application.Features.Inventory;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Inventory;

public interface IInventoryItemRepository : IRepository<InventoryItem, Guid>
{
    // Catalog view, name order. includeInactive for management/display lookups
    // (historical movements may reference deactivated items).
    Task<IReadOnlyList<InventoryItem>> ListAsync(
        bool includeInactive = false, CancellationToken ct = default);

    // Case-insensitive duplicate check within a farm; excludeId skips the item
    // being renamed.
    Task<bool> NameExistsAsync(Guid farmId, string name, Guid? excludeId = null, CancellationToken ct = default);

    // True if any lot references the item — guards unit edits (quantities
    // recorded in one unit must not be reinterpreted in another).
    Task<bool> HasLotsAsync(Guid itemId, CancellationToken ct = default);
}

// Stock roll-up for list screens: on-hand = Σ lots' QuantityAvailable.
public sealed record InventoryStock(Guid InventoryItemId, decimal QuantityOnHand);
