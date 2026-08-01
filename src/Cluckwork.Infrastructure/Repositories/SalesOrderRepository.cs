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
    // the row lock is already held by then. AccountId is IN the predicate
    // (#313): a foreign-tenant id matches no row here, so FOR UPDATE is never
    // attempted against it — the caller's post-load AccountId check (kept as
    // defense in depth) would otherwise run only after the query filter is
    // bypassed via IgnoreQueryFilters, i.e. after the lock is already waited on.
    public async Task<SalesOrder?> GetByIdLockedAsync(Guid accountId, Guid id, CancellationToken ct = default)
    {
        var order = await db.SalesOrders.FromSqlInterpolated($"""
            SELECT * FROM "SalesOrders" WHERE "Id" = {id} AND "AccountId" = {accountId} FOR UPDATE
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
