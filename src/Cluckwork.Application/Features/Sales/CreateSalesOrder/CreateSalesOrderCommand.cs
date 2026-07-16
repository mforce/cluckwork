namespace Cluckwork.Application.Features.Sales.CreateSalesOrder;

public sealed record CreateSalesOrderCommand(Guid CustomerId, DateOnly OrderDate);
