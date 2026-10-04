using Cluckwork.Application.Features.Sales;
using Cluckwork.Domain.Sales;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Repositories;

// Reads rely on the tenant query filter; the purge ignores it. The order reads
// track, as the seeder's did, so a later ConfirmSale in the same context sees
// the same instance.
public sealed class CommerceFixture(AppDbContext db) : ICommerceFixture
{
    public async Task<Guid?> FindProductIdByNameAsync(string name, CancellationToken ct = default) =>
        (await db.Products.FirstOrDefaultAsync(p => p.Name == name, ct))?.Id;

    public async Task<Guid?> FindCustomerIdByNameAsync(string name, CancellationToken ct = default) =>
        (await db.Customers.FirstOrDefaultAsync(c => c.Name == name, ct))?.Id;

    public async Task<Guid?> FindOrderIdAsync(Guid customerId, DateOnly orderDate, CancellationToken ct = default) =>
        (await db.SalesOrders
            .FirstOrDefaultAsync(o => o.CustomerId == customerId && o.OrderDate == orderDate, ct))?.Id;

    public Task<bool> OrderHasLineAsync(Guid orderId, Guid productId, CancellationToken ct = default) =>
        db.SalesOrderItems.AnyAsync(i => i.SalesOrderId == orderId && i.ProductId == productId, ct);

    public async Task<bool> IsOrderConfirmedAsync(Guid orderId, CancellationToken ct = default) =>
        (await db.SalesOrders.FirstAsync(o => o.Id == orderId, ct)).Status == SalesOrderStatus.Confirmed;

    public Task<bool> PaymentExistsAsync(Guid orderId, CancellationToken ct = default) =>
        db.Payments.AnyAsync(p => p.SalesOrderId == orderId, ct);

    public async Task<long> GetOrderTotalMinorUnitsAsync(Guid orderId, CancellationToken ct = default) =>
        (await db.SalesOrders.FirstAsync(o => o.Id == orderId, ct)).TotalAmount.MinorUnits;

    public async Task<CommerceFixtureCounts> CountAsync(CancellationToken ct = default) =>
        new(
            Customers: await db.Customers.CountAsync(ct),
            SalesOrders: await db.SalesOrders.CountAsync(ct),
            DraftOrders: await db.SalesOrders.CountAsync(o => o.Status == SalesOrderStatus.Draft, ct),
            ConfirmedOrders: await db.SalesOrders.CountAsync(o => o.Status == SalesOrderStatus.Confirmed, ct),
            ShippedOrders: await db.SalesOrders.CountAsync(o => o.Status == SalesOrderStatus.Shipped, ct),
            InvoicedOrders: await db.SalesOrders.CountAsync(o => o.Status == SalesOrderStatus.Invoiced, ct),
            CancelledOrders: await db.SalesOrders.CountAsync(o => o.Status == SalesOrderStatus.Cancelled, ct),
            VoidedOrders: await db.SalesOrders.CountAsync(o => o.Status == SalesOrderStatus.Voided, ct),
            Payments: await db.Payments.CountAsync(ct));

    public async Task PurgeOrdersAndCustomersAsync(Guid accountId, CancellationToken ct = default)
    {
        await db.SalesOrderItems.IgnoreQueryFilters().Where(x => x.AccountId == accountId).ExecuteDeleteAsync(ct);
        await db.SalesOrders.IgnoreQueryFilters().Where(x => x.AccountId == accountId).ExecuteDeleteAsync(ct);
        await db.Customers.IgnoreQueryFilters().Where(x => x.AccountId == accountId).ExecuteDeleteAsync(ct);
    }
}
