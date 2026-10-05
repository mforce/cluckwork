namespace Cluckwork.Application.Modules.Commerce.Contracts;

[ModuleContract("Commerce")]
public sealed record VoidSaleCommand(Guid SalesOrderId, string Reason);

[ModuleContract("Commerce")]
public sealed record VoidSaleResponse(Guid SalesOrderId, string Status);
