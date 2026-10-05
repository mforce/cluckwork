namespace Cluckwork.Application.Modules.GeneralInventory.Contracts;

public sealed record CreateInventoryItemCommand(
    string Name, string Category, string Unit, long? DefaultUnitCostMinorUnits);
