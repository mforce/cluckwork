namespace Cluckwork.Application.Modules.Commerce.Contracts;

[ModuleContract("Commerce")]
public sealed record CreateSalesOrderCommand(Guid CustomerId, DateOnly OrderDate);
