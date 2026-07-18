namespace Cluckwork.Application.Features.Sales;

using Cluckwork.Domain.Sales;

// Deliberately not IRepository: allocations are only ever written as a batch
// inside the confirm transaction and deleted as a batch inside the void
// transaction — no single-row mutation surface to misuse.
public interface ISalesOrderAllocationRepository
{
    Task AddRangeAsync(IReadOnlyList<SalesOrderAllocation> allocations, CancellationToken ct = default);

    // Tracked read — the void path deletes these rows after restoring the lots.
    Task<IReadOnlyList<SalesOrderAllocation>> ListByOrderAsync(Guid salesOrderId, CancellationToken ct = default);

    void RemoveRange(IReadOnlyList<SalesOrderAllocation> allocations);
}
