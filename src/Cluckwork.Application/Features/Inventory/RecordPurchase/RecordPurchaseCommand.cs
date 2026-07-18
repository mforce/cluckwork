namespace Cluckwork.Application.Features.Inventory.RecordPurchase;

public sealed record RecordPurchaseCommand(
    Guid InventoryItemId,
    DateOnly ReceivedDate,
    decimal Quantity,
    // Null falls back to the item's default unit cost; if neither exists the
    // purchase is rejected — a lot without a cost breaks §19 cost reporting.
    long? UnitCostMinorUnits,
    string? LotNumber,
    DateOnly? ExpiryDate,
    string? Note);
