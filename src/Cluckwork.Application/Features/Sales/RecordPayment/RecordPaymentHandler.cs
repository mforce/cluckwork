namespace Cluckwork.Application.Features.Sales.RecordPayment;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Sales;

// Records a customer payment against a confirmed order (spec §10.11).
//
// Locking: the order row is taken FOR UPDATE, then the settled total is
// summed INSIDE the transaction — two racing payments serialize on the order
// row, so the no-overpay rule cannot be overshot by concurrency; a payment
// racing the order's void serializes the same way and deterministically sees
// Voided (422) instead of paying a dead order.
public sealed class RecordPaymentHandler(
    ISalesOrderRepository salesOrders,
    IPaymentRepository payments,
    IUnitOfWork unitOfWork)
{
    public async Task<Result<Guid>> HandleAsync(
        RecordPaymentCommand command, Guid accountId, CancellationToken ct)
    {
        Result<Guid>? outcome = null;

        await unitOfWork.ExecuteInTransactionAsync(async transactionCt =>
        {
            var order = await salesOrders.GetByIdLockedAsync(command.SalesOrderId, transactionCt);
            if (order is null)
            {
                outcome = Result.Failure<Guid>(Error.NotFound(nameof(SalesOrder), command.SalesOrderId));
                return false;
            }
            if (order.AccountId != accountId)
            {
                outcome = Result.Failure<Guid>(AppError.TenantMismatch());
                return false;
            }
            if (order.Status != SalesOrderStatus.Confirmed)
            {
                outcome = Result.Failure<Guid>(Error.Domain(
                    "Payment.OrderNotConfirmed",
                    $"Payments can only be recorded against confirmed orders — this one is {order.Status.ToString().ToLowerInvariant()}."));
                return false;
            }

            var paid = await payments.SumNonVoidedByOrderAsync(order.Id, transactionCt);
            var outstanding = order.TotalAmount.MinorUnits - paid;
            if (command.AmountMinorUnits > outstanding)
            {
                outcome = Result.Failure<Guid>(Error.Validation(
                    "Payment.Overpay",
                    $"This payment exceeds the outstanding amount ({new Money(outstanding, order.TotalAmount.CurrencyCode, order.TotalAmount.CurrencyMinorUnit)})."));
                return false;
            }

            var method = Enum.Parse<PaymentMethod>(command.Method, ignoreCase: true);
            var payment = Payment.Create(
                Guid.NewGuid(), accountId, order.Id, order.CustomerId,
                command.PaymentDate, command.AmountMinorUnits,
                order.TotalAmount.CurrencyCode, order.TotalAmount.CurrencyMinorUnit,
                method, command.ReferenceNumber, command.Note);

            await payments.AddAsync(payment, transactionCt);
            outcome = Result.Success(payment.Id);
            return true;
        }, ct);

        return outcome!;
    }
}
