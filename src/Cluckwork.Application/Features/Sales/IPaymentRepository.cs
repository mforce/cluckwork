namespace Cluckwork.Application.Features.Sales;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Sales;

public interface IPaymentRepository : IRepository<Payment, Guid>
{
    // All payments of one order, oldest first (a settlement history reads
    // top-down), voided included — the SPA badges them.
    Task<IReadOnlyList<Payment>> ListByOrderAsync(Guid salesOrderId, CancellationToken ct = default);

    // Settled total for the no-overpay check. Call INSIDE the transaction that
    // holds the order row lock — the lock serializes racing payments.
    Task<long> SumNonVoidedByOrderAsync(Guid salesOrderId, CancellationToken ct = default);

    // Order-void guard: money must be voided before the order is.
    Task<bool> AnyNonVoidedByOrderAsync(Guid salesOrderId, CancellationToken ct = default);

    // Per-customer money summary across CONFIRMED orders (server-side sums —
    // clients never aggregate pages): confirmed order total and settled
    // payments; outstanding is the difference.
    Task<IReadOnlyList<CustomerBalance>> ListCustomerBalancesAsync(CancellationToken ct = default);
}

public sealed record CustomerBalance(
    Guid CustomerId, long ConfirmedTotalMinorUnits, long PaidMinorUnits);
