namespace Cluckwork.Application.Features.Sales.VoidSale;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.EggLots;
using Cluckwork.Application.Features.Eggs;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Eggs;
using Cluckwork.Domain.Sales;

// Undo of a mistaken confirm (#60): Confirmed → Voided, returning every
// allocated quantity to the exact lot it was drawn from and marking the
// allocation rows released (never deleted — they are the sale→lot
// traceability chain, spec §9.6).
//
// Locking: the order row is taken FOR UPDATE first, then the source lots in
// the same canonical (ProductionDate, Id) order the confirm path uses — so a
// void racing a void serializes on the order row (loser sees Voided → 409),
// and a void racing a confirm over the same lots serializes on the lot locks.
public sealed class VoidSaleHandler(
    ISalesOrderRepository salesOrders,
    ISalesOrderAllocationRepository allocations,
    IEggLotRepository eggLots,
    IPaymentRepository payments,
    IUnitOfWork unitOfWork,
    IEggInventoryMovementRepository eggMovements,
    IClock clock,
    IAuditWriter audit)
{
    public async Task<Result<VoidSaleResponse>> HandleAsync(
        VoidSaleCommand command, Guid accountId, CancellationToken ct)
    {
        Result<VoidSaleResponse>? outcome = null;

        await unitOfWork.ExecuteInTransactionAsync(async transactionCt =>
        {
            // Locked + re-read INSIDE the transaction: a parallel void blocks
            // here until the winner commits, then deterministically fails the
            // status check instead of racing the Version token.
            var order = await salesOrders.GetByIdLockedAsync(command.SalesOrderId, transactionCt);
            if (order is null)
            {
                outcome = Result.Failure<VoidSaleResponse>(
                    Error.NotFound(nameof(SalesOrder), command.SalesOrderId));
                return false;
            }

            if (order.AccountId != accountId)
            {
                outcome = Result.Failure<VoidSaleResponse>(AppError.TenantMismatch());
                return false;
            }

            // Money first (#89): a voided order must not keep settled payments —
            // the operator voids those explicitly (each needs its own reason)
            // before the order can go. Checked under the order row lock, so a
            // racing payment serializes and sees Voided instead.
            if (await payments.AnyNonVoidedByOrderAsync(order.Id, transactionCt))
            {
                outcome = Result.Failure<VoidSaleResponse>(Error.Domain(
                    "SalesOrder.HasPayments",
                    "This order has recorded payments — void the payments first."));
                return false;
            }

            var voidResult = order.Void(command.Reason);
            if (voidResult.IsFailure)
            {
                outcome = Result.Failure<VoidSaleResponse>(voidResult.Error);
                return false;
            }

            var rows = await allocations.ListPendingByOrderAsync(order.Id, transactionCt);
            if (rows.Count == 0)
            {
                // Orders confirmed before allocation provenance existed have no
                // rows — restoring their stock would be a guess, so refuse.
                outcome = Result.Failure<VoidSaleResponse>(Error.Domain(
                    "SalesOrder.NoAllocationRecords",
                    "This order was confirmed before lot-level allocation tracking existed, "
                    + "so its stock cannot be returned automatically. An administrator must "
                    + "adjust the affected egg lots manually if this sale did not happen."));
                return false;
            }

            // Lock the source lots (same ordering as the confirm path), then
            // restore each lot's total in one step.
            var perLot = rows
                .GroupBy(r => r.EggLotId)
                .ToDictionary(g => g.Key, g => g.Sum(r => r.Quantity));
            var lockedLots = await eggLots.GetByIdsLockedAsync(
                accountId, perLot.Keys.ToList(), transactionCt);

            if (lockedLots.Count != perLot.Count)
            {
                outcome = Result.Failure<VoidSaleResponse>(Error.Domain(
                    "EggLot.AllocationSourceMissing",
                    "One or more source egg lots for this order no longer exist."));
                return false;
            }

            foreach (var lot in lockedLots)
            {
                var restore = lot.Restore(perLot[lot.Id]);
                if (restore.IsFailure)
                {
                    outcome = Result.Failure<VoidSaleResponse>(restore.Error);
                    return false;
                }

                // Ledger row (#101): the returned eggs re-enter as an explicit
                // Void movement, same transaction as the restore.
                await eggMovements.AddAsync(EggInventoryMovement.Create(
                    Guid.NewGuid(), accountId, lot.Id, EggMovementType.Void,
                    perLot[lot.Id], nameof(SalesOrder), order.Id, clock.UtcNow,
                    reason: command.Reason), transactionCt);
            }

            var releasedAt = clock.UtcNow;
            foreach (var row in rows)
                row.MarkReleased(releasedAt);

            // Same transaction as the change (#93): rolls back with it.
            await audit.WriteAsync("SalesOrder.Void", nameof(SalesOrder), order.Id,
                command.Reason, ct: transactionCt);

            outcome = Result.Success(new VoidSaleResponse(order.Id, order.Status.ToString()));
            return true;
        }, ct);

        return outcome!;
    }
}
