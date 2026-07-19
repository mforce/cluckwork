namespace Cluckwork.Application.Features.Sales.VoidPayment;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Sales;

// Undo of a mistaken payment (#89): status flip + reason, never a delete —
// the row stays for the ledger, and the order's outstanding grows back.
public sealed class VoidPaymentHandler(
    IPaymentRepository payments,
    IUnitOfWork unitOfWork)
{
    public async Task<Result> HandleAsync(VoidPaymentCommand command, CancellationToken ct)
    {
        var payment = await payments.GetByIdAsync(command.PaymentId, ct);
        if (payment is null)
            return Result.Failure(Error.NotFound(nameof(Payment), command.PaymentId));

        // End-to-end optimistic concurrency (AGENTS.md): base-version mismatch
        // is a deterministic 409; the EF token backstops the racing-save window.
        if (payment.Version != command.Version)
            return Result.Failure(Error.Conflict(
                "Payment.VersionMismatch",
                "The payment was changed by someone else. Reload and retry."));

        var result = payment.Void(command.Reason);
        if (result.IsFailure) return result;

        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}
