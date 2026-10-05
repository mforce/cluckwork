namespace Cluckwork.Application.Modules.GeneralInventory.Contracts;

[ModuleContract("GeneralInventory")]
public sealed record UpdateInventoryItemCommand(
    Guid InventoryItemId, string Name, string Unit, long? DefaultUnitCostMinorUnits);
