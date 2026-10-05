namespace Cluckwork.Application.Modules.Commerce.Contracts;

public sealed record UpdateOrderItemCommand(
    Guid SalesOrderId, Guid ItemId, int Quantity, long UnitPriceMinorUnits);
