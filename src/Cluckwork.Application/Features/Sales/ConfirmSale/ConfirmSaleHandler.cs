namespace Cluckwork.Application.Features.Sales.ConfirmSale;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.EggLots;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Sales;

public sealed class ConfirmSaleHandler(
    ISalesOrderRepository salesOrders,
    IEggLotRepository eggLots,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    // Implements functional spec §10.9.1 pessimistic-lock FIFO allocation:
    //   BEGIN
    //     SELECT candidate egg_lots FOR UPDATE
    //     re-read quantity_available (after lock)
    //     validate allocation fits
    //     update egg_lots.quantity_available + version
    //   COMMIT  (conflict/insufficient → 409)
    public async Task<Result<ConfirmSaleResponse>> HandleAsync(
        ConfirmSaleCommand command, Guid accountId, CancellationToken ct)
    {
        var order = await salesOrders.GetByIdAsync(command.SalesOrderId, ct);
        if (order is null)
            return Result.Failure<ConfirmSaleResponse>(
                Error.NotFound(nameof(SalesOrder), command.SalesOrderId));

        if (order.AccountId != accountId)
            return Result.Failure<ConfirmSaleResponse>(AppError.TenantMismatch());

        var allocationDate = clock.TodayUtc;
        Result<ConfirmSaleResponse>? failure = null;

        await unitOfWork.ExecuteInTransactionAsync(async transactionCt =>
        {
            foreach (var item in order.Items)
            {
                var lockedLots = await eggLots.GetAvailableFifoLockedAsync(
                    accountId, item.GradeCode, allocationDate, transactionCt);

                var remaining = item.Quantity;
                foreach (var lot in lockedLots)
                {
                    if (remaining <= 0) break;
                    var take = Math.Min(remaining, lot.QuantityAvailable);
                    var alloc = lot.Allocate(take, allocationDate);
                    if (alloc.IsFailure)
                    {
                        failure = Result.Failure<ConfirmSaleResponse>(alloc.Error);
                        return false;
                    }
                    remaining -= take;
                }

                if (remaining > 0)
                {
                    failure = Result.Failure<ConfirmSaleResponse>(Error.Domain(
                        "EggLot.InsufficientStock",
                        $"Insufficient stock for grade {item.GradeCode}: {remaining} units unallocated."));
                    return false;
                }
            }

            var confirmResult = order.Confirm();
            if (confirmResult.IsFailure)
            {
                failure = Result.Failure<ConfirmSaleResponse>(confirmResult.Error);
                return false;
            }

            return true;
        }, ct);

        if (failure is not null)
            return failure;

        return Result.Success(new ConfirmSaleResponse(order.Id, order.Status.ToString()));
    }
}
