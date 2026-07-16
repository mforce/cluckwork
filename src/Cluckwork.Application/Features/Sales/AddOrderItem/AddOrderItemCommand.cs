namespace Cluckwork.Application.Features.Sales.AddOrderItem;

public sealed record AddOrderItemCommand(
    Guid SalesOrderId, Guid EggGradeId, int Quantity, long UnitPriceMinorUnits);
