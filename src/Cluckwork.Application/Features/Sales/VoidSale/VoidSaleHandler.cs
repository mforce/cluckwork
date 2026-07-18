namespace Cluckwork.Application.Features.Sales.VoidSale;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.EggLots;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Sales;

// Undo of a mistaken confirm (#60): Confirmed → Voided, returning every
// allocated quantity to the exact lot it was drawn from. Mirrors the confirm
// path's pessimistic locking (functional spec §10.9.1) so a void racing a
// confirm that draws from the same lots serializes on FOR UPDATE instead of
// losing updates. A void racing another mutation of the same ORDER is caught
// by the Version concurrency token at commit (global handler → 409).
public sealed class VoidSaleHandler(
    ISalesOrderRepository salesOrders,
    ISalesOrderAllocationRepository allocations,
    IEggLotRepository eggLots,
    IUnitOfWork unitOfWork)
{
    public async Task<Result<VoidSaleResponse>> HandleAsync(
        VoidSaleCommand command, Guid accountId, CancellationToken ct)
    {
        var order = await salesOrders.GetByIdAsync(command.SalesOrderId, ct);
        if (order is null)
            return Result.Failure<VoidSaleResponse>(
                Error.NotFound(nameof(SalesOrder), command.SalesOrderId));

        if (order.AccountId != accountId)
            return Result.Failure<VoidSaleResponse>(AppError.TenantMismatch());

        Result<VoidSaleResponse>? failure = null;

        await unitOfWork.ExecuteInTransactionAsync(async transactionCt =>
        {
            var voidResult = order.Void(command.Reason);
            if (voidResult.IsFailure)
            {
                failure = Result.Failure<VoidSaleResponse>(voidResult.Error);
                return false;
            }

            var rows = await allocations.ListByOrderAsync(order.Id, transactionCt);
            if (rows.Count == 0)
            {
                // Orders confirmed before allocation provenance existed have no
                // rows — restoring their stock would be a guess, so refuse.
                failure = Result.Failure<VoidSaleResponse>(Error.Domain(
                    "SalesOrder.NoAllocationRecords",
                    "This order predates lot-level allocation tracking and cannot be voided."));
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
                failure = Result.Failure<VoidSaleResponse>(Error.Domain(
                    "EggLot.AllocationSourceMissing",
                    "One or more source egg lots for this order no longer exist."));
                return false;
            }

            foreach (var lot in lockedLots)
            {
                var restore = lot.Restore(perLot[lot.Id]);
                if (restore.IsFailure)
                {
                    failure = Result.Failure<VoidSaleResponse>(restore.Error);
                    return false;
                }
            }

            allocations.RemoveRange(rows);
            return true;
        }, ct);

        if (failure is not null)
            return failure;

        return Result.Success(new VoidSaleResponse(order.Id, order.Status.ToString()));
    }
}
