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
    // committed status instead of losing a Version race at commit.
    Task<SalesOrder?> GetByIdLockedAsync(Guid id, CancellationToken ct = default);

    // Paged, newest-first, items included. Optional status/customer/date filters.
    Task<IReadOnlyList<SalesOrder>> ListAsync(
        SalesOrderStatus? status, Guid? customerId, DateOnly? from, DateOnly? to,
        int limit, int offset, CancellationToken ct = default);
}
