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

    public async Task<IReadOnlyList<SalesOrderListRow>> ListAsync(
        SalesOrderListFilter filter, int limit, int offset, CancellationToken ct = default)
    {
        if (filter.Settlement == SettlementScope.Hidden)
        {
            var page = await HiddenPage(filter, limit, offset).ToListAsync(ct);
            return page.Select(o => new SalesOrderListRow(o, null)).ToList();
        }

        return await SettlementPage(filter, limit, offset).ToListAsync(ct);
    }

    // The SQL ListAsync would run, for SalesOrderListQueryTests — a model-only
    // guard, no database. Returning the string rather than the IQueryable keeps
    // the two branches' differing element types out of one signature.
    public string ListQuerySql(SalesOrderListFilter filter, int limit, int offset) =>
        filter.Settlement == SettlementScope.Hidden
            ? HiddenPage(filter, limit, offset).ToQueryString()
            : SettlementPage(filter, limit, offset).ToQueryString();

    // Locals, not filter.X in the expression tree: EF parameterises a captured
    // member the same way it parameterises a local, and keeping the shape
    // identical to the pre-#769 query keeps its emitted SQL identical too.
    private IQueryable<SalesOrder> Filtered(SalesOrderListFilter filter)
    {
        var status = filter.Status;
        var customerId = filter.CustomerId;
        var from = filter.From;
        var to = filter.To;
        return db.SalesOrders
            .AsNoTracking()
            .Include(o => o.Items)
            .Where(o => (status == null || o.Status == status)
                     && (customerId == null || o.CustomerId == customerId)
                     && (from == null || o.OrderDate >= from)
                     && (to == null || o.OrderDate <= to));
    }

    // #769 SettlementScope.Hidden — the pre-#769 query, untouched. A caller
    // outside the money tier gets SQL that never names Payments; the null
    // outstanding is then a fact about the query, not a field blanked after it.
    private IQueryable<SalesOrder> HiddenPage(SalesOrderListFilter filter, int limit, int offset) =>
        Filtered(filter)
            .OrderByDescending(o => o.OrderDate).ThenByDescending(o => o.Id)
            .Skip(offset)
            .Take(limit);

    // #769 — one query for the page AND its settlement figures. The figure is
    // ONE C# expression used for both the value and the unpaid predicate, so
    // the two cannot disagree about what "unpaid" means.
    //
    // CASE WHEN "Status" = Confirmed THEN total - (correlated sum) END yields
    // SQL NULL for every other status, and `NULL > 0` is not true — so Draft,
    // Cancelled and Voided fall out of the unpaid filter structurally rather
    // than through a second hand-written status predicate that could drift
    // from the one above it. Keep that shape.
    //
    // Include(o => o.Items) survives this wrapper projection: LIMIT/OFFSET
    // apply in an inner subquery before the items LEFT JOIN, so paging counts
    // ORDERS and not order lines. The tenant query filter reaches the Payments
    // subquery on its own. Both pinned by SalesOrderListQueryTests.
    private IQueryable<SalesOrderListRow> SettlementPage(
        SalesOrderListFilter filter, int limit, int offset)
    {
        // Ordered before the projection and projected into an ANONYMOUS type,
        // both forced by the translator rather than chosen. Ordering the
        // projection by `x.Order.OrderDate`, and filtering it by
        // `x.OutstandingMinorUnits`, each inline the whole `new
        // SalesOrderListRow(...)` into the ORDER BY / WHERE and EF refuses to
        // translate the member access back off a positional record whose other
        // member is an entity. It reduces member access off an anonymous type
        // without complaint, so the figure below is still written ONCE and the
        // filter is literally that same expression.
        var rows = Filtered(filter)
            .OrderByDescending(o => o.OrderDate).ThenByDescending(o => o.Id)
            .Select(o => new
            {
                Order = o,
                Outstanding = o.Status == SalesOrderStatus.Confirmed
                    ? o.TotalAmount.MinorUnits - db.Payments
                        .Where(p => p.SalesOrderId == o.Id && !p.Voided)
                        .Sum(p => p.AmountMinorUnits)
                    : (long?)null,
            });

        if (filter.Settlement == SettlementScope.UnpaidOnly)
            rows = rows.Where(x => x.Outstanding > 0);

        return rows
            .Select(x => new SalesOrderListRow(x.Order, x.Outstanding))
            .Skip(offset)
            .Take(limit);
    }

    public async Task AddAsync(SalesOrder entity, CancellationToken ct = default) =>
        await db.SalesOrders.AddAsync(entity, ct);

    public void Update(SalesOrder entity) => db.SalesOrders.Update(entity);

    public void Remove(SalesOrder entity) => db.SalesOrders.Remove(entity);
}
