namespace Cluckwork.Domain.Modules.Commerce.Sales;

public sealed record SalesOrderConfirmedEvent(Guid OrderId, Guid AccountId) : IDomainEvent;
