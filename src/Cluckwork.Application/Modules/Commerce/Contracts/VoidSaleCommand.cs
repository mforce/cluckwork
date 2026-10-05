namespace Cluckwork.Application.Modules.Commerce.Contracts;

public sealed record VoidSaleCommand(Guid SalesOrderId, string Reason);

public sealed record VoidSaleResponse(Guid SalesOrderId, string Status);
