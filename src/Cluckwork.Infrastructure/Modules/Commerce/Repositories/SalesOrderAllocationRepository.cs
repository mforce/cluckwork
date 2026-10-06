using Cluckwork.Application.Modules.Commerce.Sales;
using Cluckwork.Domain.Modules.Commerce.Sales;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Modules.Commerce.Repositories;

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
