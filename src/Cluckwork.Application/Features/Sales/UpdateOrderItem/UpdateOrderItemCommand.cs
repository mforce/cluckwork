namespace Cluckwork.Application.Features.Sales.UpdateOrderItem;

[ModuleContract("Commerce")]
public sealed record UpdateOrderItemCommand(
    Guid SalesOrderId, Guid ItemId, int Quantity, long UnitPriceMinorUnits);
