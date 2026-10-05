namespace Cluckwork.Application.Features.Inventory.CreateInventoryItem;

[ModuleContract("GeneralInventory")]
public sealed record CreateInventoryItemCommand(
    string Name, string Category, string Unit, long? DefaultUnitCostMinorUnits);
