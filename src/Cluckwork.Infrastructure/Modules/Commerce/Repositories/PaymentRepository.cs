using Cluckwork.Application.Modules.Commerce.Contracts;
using Cluckwork.Application.Modules.Commerce.Sales;
using Cluckwork.Application.Modules.Farm.Accounts;
using Cluckwork.Domain.Modules.Commerce.Contracts;
using Cluckwork.Domain.Modules.Commerce.Sales;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Modules.Commerce.Repositories;

public sealed class PaymentRepository(AppDbContext db) : IPaymentRepository,
    ICurrencyBoundRowSource
{
    Task<bool> ICurrencyBoundRowSource.AnyAsync(CancellationToken ct) =>
        db.Payments.AnyAsync(ct);

    // Reads rely on the tenant query filter (AccountId == current tenant).
    public Task<Payment?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.Payments.FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<IReadOnlyList<Payment>> ListByOrderAsync(
        Guid salesOrderId, CancellationToken ct = default) =>
        await db.Payments
            .AsNoTracking()
            .Where(p => p.SalesOrderId == salesOrderId)
            .OrderByBusinessChronology(p => p.PaymentDate)
            .ToListAsync(ct);

    public async Task<long> SumNonVoidedByOrderAsync(
        Guid salesOrderId, CancellationToken ct = default) =>
        await db.Payments
            .Where(p => p.SalesOrderId == salesOrderId && !p.Voided)
            .SumAsync(p => p.AmountMinorUnits, ct);

    public Task<bool> AnyNonVoidedByOrderAsync(
        Guid salesOrderId, CancellationToken ct = default) =>
        db.Payments.AnyAsync(p => p.SalesOrderId == salesOrderId && !p.Voided, ct);

    public async Task<IReadOnlyList<CustomerBalance>> ListCustomerBalancesAsync(
        CancellationToken ct = default) =>
        await Balances().ToListAsync(ct);

    public async Task<IReadOnlyList<CustomerBalance>> ListCustomerBalancesAsync(
        int limit, int offset, CancellationToken ct = default) =>
        await Balances().Skip(offset).Take(limit).ToListAsync(ct);

    // Confirmed order totals and settled payments per customer, each grouped
    // in its own subquery and then joined, because joining the raw rows would
    // double-count on the many-to-many of orders × payments. Largest
    // outstanding first; the customer id breaks ties so pages are stable.
    private IQueryable<CustomerBalance> Balances()
    {
        var confirmed = db.SalesOrders
            .Where(o => o.Status == SalesOrderStatus.Confirmed)
            .GroupBy(o => o.CustomerId)
            .Select(g => new { CustomerId = g.Key, Total = g.Sum(o => o.TotalAmount.MinorUnits) });

        var paid = db.Payments
            .Where(p => !p.Voided)
            .GroupBy(p => p.CustomerId)
            .Select(g => new { CustomerId = g.Key, Total = g.Sum(p => p.AmountMinorUnits) });

        return from c in confirmed
               join p in paid on c.CustomerId equals p.CustomerId into settled
               from p in settled.DefaultIfEmpty()
               select new { c.CustomerId, c.Total, Paid = (long?)p!.Total ?? 0 } into b
               orderby b.Total - b.Paid descending, b.CustomerId
               select new CustomerBalance(b.CustomerId, b.Total, b.Paid);
    }

    public async Task AddAsync(Payment entity, CancellationToken ct = default) =>
        await db.Payments.AddAsync(entity, ct);
}
