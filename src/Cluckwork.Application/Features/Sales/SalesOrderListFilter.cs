using Cluckwork.Domain.Sales;

namespace Cluckwork.Application.Features.Sales;

[ModuleContract("Commerce")]
public sealed record SalesOrderListFilter(
    SalesOrderStatus? Status, Guid? CustomerId, DateOnly? From, DateOnly? To,
    SettlementScope Settlement);
