namespace Cluckwork.Application.Modules.GeneralInventory.Contracts;

[ModuleContract("GeneralInventory")]
public sealed record CreateInventoryItemCommand(
    string Name, string Category, string Unit, long? DefaultUnitCostMinorUnits);
