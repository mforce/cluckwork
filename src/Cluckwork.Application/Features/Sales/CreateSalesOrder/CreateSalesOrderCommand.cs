namespace Cluckwork.Application.Features.Sales.CreateSalesOrder;

[ModuleContract("Commerce")]
public sealed record CreateSalesOrderCommand(Guid CustomerId, DateOnly OrderDate);
