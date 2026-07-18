namespace Cluckwork.Application.Features.Sales;

using Cluckwork.Domain.Sales;

// Deliberately not IRepository: allocations are written as a batch inside the
// confirm transaction and only ever mutated by marking them released inside
// the void transaction. Rows are never deleted — they are the traceability
// chain from a sale back to its source lots (spec §9.6).
public interface ISalesOrderAllocationRepository
{
    Task AddRangeAsync(IReadOnlyList<SalesOrderAllocation> allocations, CancellationToken ct = default);

    // Tracked read of the live (unreleased) allocations — the void path marks
    // these released after restoring the lots.
    Task<IReadOnlyList<SalesOrderAllocation>> ListPendingByOrderAsync(
        Guid salesOrderId, CancellationToken ct = default);
}
