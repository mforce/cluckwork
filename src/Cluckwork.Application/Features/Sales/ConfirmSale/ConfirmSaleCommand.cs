namespace Cluckwork.Application.Features.Sales.ConfirmSale;

public sealed record ConfirmSaleCommand(Guid SalesOrderId);

public sealed record ConfirmSaleResponse(Guid SalesOrderId, string Status);
