namespace Cluckwork.Application.Modules.GeneralInventory.Contracts;

public sealed record UpdateInventoryItemCommand(
    Guid InventoryItemId, string Name, string Unit, long? DefaultUnitCostMinorUnits);
