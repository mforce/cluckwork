namespace Cluckwork.Application.Features.Inventory.UpdateInventoryItem;

public sealed record UpdateInventoryItemCommand(
    Guid InventoryItemId, string Name, string Unit, long? DefaultUnitCostMinorUnits);
