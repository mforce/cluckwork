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

    public Task<SalesOrder?> GetReadOnlyAsync(Guid id, CancellationToken ct = default) =>
        db.SalesOrders
            .AsNoTracking()
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == id, ct);

    // FOR UPDATE row lock + fresh load (call inside an open transaction). The
    // raw query can't compose with Include, so items load in a second step —
    // the row lock is already held by then. Tenant scoping is the caller's
    // job (the handler checks AccountId), hence IgnoreQueryFilters like the
    // other locked reads.
    public async Task<SalesOrder?> GetByIdLockedAsync(Guid id, CancellationToken ct = default)
    {
        var order = await db.SalesOrders.FromSqlInterpolated($"""
            SELECT * FROM "SalesOrders" WHERE "Id" = {id} FOR UPDATE
            """)
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(ct);
        if (order is null) return null;

        await db.Entry(order).Collection(o => o.Items).LoadAsync(ct);
        return order;
    }

    public async Task<IReadOnlyList<SalesOrder>> ListAsync(
        SalesOrderStatus? status, Guid? customerId, DateOnly? from, DateOnly? to,
        int limit, int offset, CancellationToken ct = default) =>
        await db.SalesOrders
            .AsNoTracking()
            .Include(o => o.Items)
            .Where(o => (status == null || o.Status == status)
                     && (customerId == null || o.CustomerId == customerId)
                     && (from == null || o.OrderDate >= from)
                     && (to == null || o.OrderDate <= to))
            .OrderByDescending(o => o.OrderDate).ThenByDescending(o => o.Id)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

    public async Task AddAsync(SalesOrder entity, CancellationToken ct = default) =>
        await db.SalesOrders.AddAsync(entity, ct);

    public void Update(SalesOrder entity) => db.SalesOrders.Update(entity);

    public void Remove(SalesOrder entity) => db.SalesOrders.Remove(entity);
}
