using Cluckwork.Domain.Modules.Commerce.Contracts;

namespace Cluckwork.Application.Modules.Commerce.Contracts;

public sealed record SalesOrderListFilter(
    SalesOrderStatus? Status, Guid? CustomerId, DateOnly? From, DateOnly? To,
    SettlementScope Settlement);
