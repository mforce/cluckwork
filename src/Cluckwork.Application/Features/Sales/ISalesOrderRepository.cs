using Cluckwork.Domain.Sales;

namespace Cluckwork.Application.Features.Sales;

public interface ISalesOrderRepository
{
    Task<SalesOrder?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task AddAsync(SalesOrder entity, CancellationToken ct = default);

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

// The order plus what it still owes: confirmed total − non-voided payments.
// NULL for anything not Confirmed (payments attach to confirmed orders only,
// so "outstanding" is undefined there and a 0 would read as settled) and for a
// caller outside the money tier.
public sealed record SalesOrderListRow(SalesOrder Order, long? OutstandingMinorUnits);
