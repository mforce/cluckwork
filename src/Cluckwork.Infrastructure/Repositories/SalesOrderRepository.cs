namespace Cluckwork.Infrastructure.Repositories;

using Cluckwork.Application.Features.Sales;
using Cluckwork.Domain.Sales;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed class SalesOrderRepository(AppDbContext db) : ISalesOrderRepository
{
    public Task<SalesOrder?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.SalesOrders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == id, ct);

    public async Task AddAsync(SalesOrder entity, CancellationToken ct = default) =>
        await db.SalesOrders.AddAsync(entity, ct);

    public void Update(SalesOrder entity) => db.SalesOrders.Update(entity);

    public void Remove(SalesOrder entity) => db.SalesOrders.Remove(entity);
}
