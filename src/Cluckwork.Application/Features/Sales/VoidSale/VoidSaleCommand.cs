namespace Cluckwork.Application.Features.Sales.VoidSale;

[ModuleContract("Commerce")]
public sealed record VoidSaleCommand(Guid SalesOrderId, string Reason);

[ModuleContract("Commerce")]
public sealed record VoidSaleResponse(Guid SalesOrderId, string Status);
