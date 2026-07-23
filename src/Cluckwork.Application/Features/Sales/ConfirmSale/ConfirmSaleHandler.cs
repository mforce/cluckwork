namespace Cluckwork.Application.Features.Sales.ConfirmSale;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.EggGrades;
using Cluckwork.Application.Features.EggLots;
using Cluckwork.Application.Features.Eggs;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Eggs;
using Cluckwork.Domain.Sales;

public sealed class ConfirmSaleHandler(
    ISalesOrderRepository salesOrders,
    IEggLotRepository eggLots,
    IEggGradeRepository eggGrades,
    ISalesOrderAllocationRepository allocations,
    IEggInventoryMovementRepository eggMovements,
    IUnitOfWork unitOfWork,
    IClock clock,
    IFarmClock farmClock)
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

        // #35: farm-local, matching the stock read's restriction boundary —
        // allocating against a UTC date would let a sale draw from a lot the
        // farm still has under medication withdrawal.
        var allocationDate = await farmClock.TodayAsync(ct);
        Result<ConfirmSaleResponse>? failure = null;

        await unitOfWork.ExecuteInTransactionAsync(async transactionCt =>
        {
            // Lot-level provenance (#60): recorded so a void can return the exact
            // quantities to the exact lots they were drawn from.
            var allocationRows = new List<SalesOrderAllocation>();

            // ONE locked statement for every grade on the order — canonical
            // (ProductionDate, Id) lock order shared with the void path, so the
            // two can never deadlock on overlapping lots.
            var gradeIds = order.Items.Select(i => i.EggGradeId).Distinct().ToList();
            var lockedLots = await eggLots.GetAvailableFifoLockedAsync(
                accountId, gradeIds, allocationDate, transactionCt);

            foreach (var item in order.Items)
            {
                // Allocation always runs in individual eggs (spec §10.5
                // quantity_base) — the line's packed-unit math is already
                // snapshotted on the item.
                var remaining = item.QuantityBase;
                // Filtering preserves the FIFO order; QuantityAvailable already
                // reflects earlier items' draws (same tracked instances).
                foreach (var lot in lockedLots.Where(l => l.EggGradeId == item.EggGradeId))
                {
                    if (remaining <= 0) break;
                    if (lot.QuantityAvailable == 0) continue;
                    var take = Math.Min(remaining, lot.QuantityAvailable);
                    var alloc = lot.Allocate(take, allocationDate);
                    if (alloc.IsFailure)
                    {
                        failure = Result.Failure<ConfirmSaleResponse>(alloc.Error);
                        return false;
                    }
                    var allocation = SalesOrderAllocation.Create(
                        accountId, order.Id, item.Id, lot.Id, take);
                    allocationRows.Add(allocation);
                    // Ledger row (#101): the draw leaves the lot as an explicit
                    // Sale movement, same transaction. It references the
                    // ALLOCATION, not the order — two same-grade lines drawing
                    // from one lot stay distinguishable, completing the §9.6
                    // chain movement → allocation → item → order (codex #102).
                    await eggMovements.AddAsync(EggInventoryMovement.Create(
                        Guid.NewGuid(), accountId, lot.Id, EggMovementType.Sale,
                        -take, nameof(SalesOrderAllocation), allocation.Id, clock.UtcNow), transactionCt);
                    remaining -= take;
                }

                if (remaining > 0)
                {
                    // Human-readable grade name for operators; fall back to the id
                    // if the grade row is unexpectedly missing.
                    var gradeName = (await eggGrades.GetByIdAsync(item.EggGradeId, transactionCt))?.Name
                        ?? item.EggGradeId.ToString();
                    failure = Result.Failure<ConfirmSaleResponse>(Error.Domain(
                        "EggLot.InsufficientStock",
                        $"Insufficient stock for grade '{gradeName}': {remaining} eggs unallocated."));
                    return false;
                }
            }

            var confirmResult = order.Confirm();
            if (confirmResult.IsFailure)
            {
                failure = Result.Failure<ConfirmSaleResponse>(confirmResult.Error);
                return false;
            }

            await allocations.AddRangeAsync(allocationRows, transactionCt);
            return true;
        }, ct);

        if (failure is not null)
            return failure;

        return Result.Success(new ConfirmSaleResponse(order.Id, order.Status.ToString()));
    }
}
