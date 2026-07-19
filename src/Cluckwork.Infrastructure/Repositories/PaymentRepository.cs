namespace Cluckwork.Infrastructure.Repositories;

using Cluckwork.Application.Features.Sales;
using Cluckwork.Domain.Sales;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed class PaymentRepository(AppDbContext db) : IPaymentRepository
{
    // Reads rely on the tenant query filter (AccountId == current tenant).
    public Task<Payment?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.Payments.FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<IReadOnlyList<Payment>> ListByOrderAsync(
        Guid salesOrderId, CancellationToken ct = default) =>
        await db.Payments
            .AsNoTracking()
            .Where(p => p.SalesOrderId == salesOrderId)
            // Id tiebreaker keeps same-day payments in a stable order.
            .OrderBy(p => p.PaymentDate).ThenBy(p => p.Id)
            .ToListAsync(ct);

    public async Task<long> SumNonVoidedByOrderAsync(
        Guid salesOrderId, CancellationToken ct = default) =>
        await db.Payments
            .Where(p => p.SalesOrderId == salesOrderId && !p.Voided)
            .SumAsync(p => p.AmountMinorUnits, ct);

    public Task<bool> AnyNonVoidedByOrderAsync(
        Guid salesOrderId, CancellationToken ct = default) =>
        db.Payments.AnyAsync(p => p.SalesOrderId == salesOrderId && !p.Voided, ct);

    // Grouped in SQL: confirmed order totals and settled payments per
    // customer. Two grouped subqueries stitched client-side over the (small)
    // customer set — a single SQL join would double-count on the many-to-many
    // of orders × payments.
    public async Task<IReadOnlyList<CustomerBalance>> ListCustomerBalancesAsync(
        CancellationToken ct = default)
    {
        var confirmed = await db.SalesOrders
            .Where(o => o.Status == SalesOrderStatus.Confirmed)
            .GroupBy(o => o.CustomerId)
            .Select(g => new { CustomerId = g.Key, Total = g.Sum(o => o.TotalAmount.MinorUnits) })
            .ToListAsync(ct);

        var paid = await db.Payments
            .Where(p => !p.Voided)
            .GroupBy(p => p.CustomerId)
            .Select(g => new { CustomerId = g.Key, Total = g.Sum(p => p.AmountMinorUnits) })
            .ToListAsync(ct);

        var paidByCustomer = paid.ToDictionary(x => x.CustomerId, x => x.Total);
        return confirmed
            .Select(c => new CustomerBalance(
                c.CustomerId, c.Total, paidByCustomer.GetValueOrDefault(c.CustomerId)))
            .ToList();
    }

    public async Task AddAsync(Payment entity, CancellationToken ct = default) =>
        await db.Payments.AddAsync(entity, ct);

    public void Update(Payment entity) => db.Payments.Update(entity);

    public void Remove(Payment entity) => db.Payments.Remove(entity);
}
