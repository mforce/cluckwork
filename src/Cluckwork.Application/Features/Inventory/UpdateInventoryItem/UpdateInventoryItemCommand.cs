namespace Cluckwork.Application.Features.Inventory.UpdateInventoryItem;

[ModuleContract("GeneralInventory")]
public sealed record UpdateInventoryItemCommand(
    Guid InventoryItemId, string Name, string Unit, long? DefaultUnitCostMinorUnits);
