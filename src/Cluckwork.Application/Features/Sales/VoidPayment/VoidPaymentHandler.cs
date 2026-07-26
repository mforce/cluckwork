namespace Cluckwork.Application.Features.Sales.VoidPayment;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Sales;
using Microsoft.Extensions.Logging;

// Undo of a mistaken payment (#89): status flip + reason, never a delete —
// the row stays for the ledger, and the order's outstanding grows back.
public sealed class VoidPaymentHandler(
    IPaymentRepository payments,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    ILogger<VoidPaymentHandler> logger)
{
    public async Task<Result> HandleAsync(VoidPaymentCommand command, CancellationToken ct)
    {
        var payment = await payments.GetByIdAsync(command.PaymentId, ct);
        if (payment is null)
            return Result.Failure(Error.NotFound(nameof(Payment), command.PaymentId))
                .LogFailure(logger, "VoidPayment");

        // End-to-end optimistic concurrency (AGENTS.md): base-version mismatch
        // is a deterministic 409; the EF token backstops the racing-save window.
        if (payment.Version != command.Version)
            return Result.Failure(Error.Conflict(
                "Payment.VersionMismatch",
                "The payment was changed by someone else. Reload and retry.")).LogFailure(logger, "VoidPayment");

        var result = payment.Void(command.Reason);
        if (result.IsFailure) return result.LogFailure(logger, "VoidPayment");

        // Same SaveChanges as the change (#93): commits or fails with it.
        await audit.WriteAsync("Payment.Void", nameof(Payment), payment.Id,
            command.Reason,
            new { payment.SalesOrderId, payment.AmountMinorUnits, payment.CurrencyCode }, ct);

        await unitOfWork.SaveChangesAsync(ct);
        logger.LogInformation(
            "Payment {PaymentId} voided against order {SalesOrderId}", payment.Id, payment.SalesOrderId);
        return Result.Success();
    }
}
