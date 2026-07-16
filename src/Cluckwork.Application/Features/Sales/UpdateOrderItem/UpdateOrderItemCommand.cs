namespace Cluckwork.Application.Features.Sales.UpdateOrderItem;

public sealed record UpdateOrderItemCommand(
    Guid SalesOrderId, Guid ItemId, int Quantity, long UnitPriceMinorUnits);
