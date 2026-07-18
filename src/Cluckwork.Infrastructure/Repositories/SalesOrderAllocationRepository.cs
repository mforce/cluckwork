namespace Cluckwork.Infrastructure.Repositories;

using Cluckwork.Application.Features.Sales;
using Cluckwork.Domain.Sales;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed class SalesOrderAllocationRepository(AppDbContext db) : ISalesOrderAllocationRepository
{
    public async Task AddRangeAsync(
        IReadOnlyList<SalesOrderAllocation> allocations, CancellationToken ct = default) =>
        await db.SalesOrderAllocations.AddRangeAsync(allocations, ct);

    public async Task<IReadOnlyList<SalesOrderAllocation>> ListPendingByOrderAsync(
        Guid salesOrderId, CancellationToken ct = default) =>
        await db.SalesOrderAllocations
            .Where(a => a.SalesOrderId == salesOrderId && a.ReleasedOnUtc == null)
            .ToListAsync(ct);
}
