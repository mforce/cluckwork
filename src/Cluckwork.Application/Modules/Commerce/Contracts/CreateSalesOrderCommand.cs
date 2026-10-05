namespace Cluckwork.Application.Modules.Commerce.Contracts;

public sealed record CreateSalesOrderCommand(Guid CustomerId, DateOnly OrderDate);
