namespace Cluckwork.Application.Features.Sales;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Sales;

public interface ISalesOrderRepository : IRepository<SalesOrder, Guid>
{
    // Untracked read for GET endpoints (the tracked GetByIdAsync is the write path).
    Task<SalesOrder?> GetReadOnlyAsync(Guid id, CancellationToken ct = default);

    // Tracked read under a FOR UPDATE row lock — call inside an open
    // transaction. Serializes state transitions of the same order: a void
    // racing a void blocks here and then deterministically sees the winner's
    // committed status instead of losing a Version race at commit. accountId
    // is part of the lock predicate itself (#313) — a foreign-tenant id must
    // never even attempt the row lock, let alone wait on it.
    Task<SalesOrder?> GetByIdLockedAsync(Guid accountId, Guid id, CancellationToken ct = default);

    // Paged, newest-first, items included. Optional status/customer/date
    // filters, plus the settlement scope the caller's tier earns (#769).
    Task<IReadOnlyList<SalesOrderListRow>> ListAsync(
        SalesOrderListFilter filter, int limit, int offset, CancellationToken ct = default);
}

// #769 — how much of the settlement figure this read may touch. An enum rather
// than a `bool includeOutstanding` + `bool unpaidOnly` pair because that pair
// makes "hide the money but filter by it" representable, and answering a
// question the caller may not see the answer to is the defect this issue
// exists to end.
public enum SettlementScope
{
    // No settlement figure and no settlement predicate. The query must not
    // name Payments at all — a structural gate, not a null applied afterwards.
    Hidden,

    // Every row carries its outstanding figure; no settlement predicate.
    Visible,

    // Every row carries its outstanding figure AND the page is restricted to
    // orders still owing money.
    UnpaidOnly,
}

public sealed record SalesOrderListFilter(
    SalesOrderStatus? Status, Guid? CustomerId, DateOnly? From, DateOnly? To,
    SettlementScope Settlement);

// The order plus what it still owes: confirmed total − non-voided payments.
// NULL for anything not Confirmed (payments attach to confirmed orders only,
// so "outstanding" is undefined there and a 0 would read as settled) and for a
// caller outside the money tier.
public sealed record SalesOrderListRow(SalesOrder Order, long? OutstandingMinorUnits);
