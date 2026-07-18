namespace Cluckwork.Application.Features.Inventory.RecordAdjustment;

// Type: "Adjustment" (signed correction) or "Discard" (write-off, negative
// only). Reason is mandatory — corrections without a why are audit holes.
public sealed record RecordAdjustmentCommand(
    Guid InventoryItemId,
    Guid InventoryLotId,
    DateOnly Date,
    string Type,
    decimal QuantityDelta,
    string Reason);
