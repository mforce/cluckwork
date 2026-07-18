namespace Cluckwork.Application.Features.Inventory.CreateInventoryItem;

public sealed record CreateInventoryItemCommand(
    string Name, string Category, string Unit, long? DefaultUnitCostMinorUnits);
