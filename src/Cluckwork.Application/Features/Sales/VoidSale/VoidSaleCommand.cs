namespace Cluckwork.Application.Features.Sales.VoidSale;

public sealed record VoidSaleCommand(Guid SalesOrderId, string Reason);

public sealed record VoidSaleResponse(Guid SalesOrderId, string Status);
