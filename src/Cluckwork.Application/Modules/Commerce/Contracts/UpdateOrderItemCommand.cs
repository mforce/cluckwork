namespace Cluckwork.Application.Modules.Commerce.Contracts;

[ModuleContract("Commerce")]
public sealed record UpdateOrderItemCommand(
    Guid SalesOrderId, Guid ItemId, int Quantity, long UnitPriceMinorUnits);
