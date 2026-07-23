namespace Cluckwork.Application.Features.Inventory.RecordAdjustment;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Inventory;

// Stock correction (#66 part 2): fixes a typo'd purchase or writes off
// spoiled feed via a compensating ledger row — the original movement is never
// edited (append-only, spec §12.3). Lot is taken FOR UPDATE so corrections
// serialize against concurrent usage draining the same lot.
public sealed class RecordAdjustmentHandler(
    IInventoryItemRepository items,
    IInventoryLotRepository lots,
    IInventoryMovementRepository movements,
    IUnitOfWork unitOfWork,
    IClock clock,
    IFarmClock farmClock,
    IAuditWriter audit)
{
    public async Task<Result<Guid>> HandleAsync(
        RecordAdjustmentCommand command, Guid accountId, CancellationToken ct)
    {
        var item = await items.GetByIdAsync(command.InventoryItemId, ct);
        if (item is null)
            return Result.Failure<Guid>(Error.NotFound(nameof(InventoryItem), command.InventoryItemId));

        if (command.Date > await farmClock.TodayAsync(ct))
            return Result.Failure<Guid>(Error.Validation(
                "InventoryMovement.FutureDate", "Adjustment date cannot be in the future."));

        Result<Guid>? outcome = null;

        await unitOfWork.ExecuteInTransactionAsync(async transactionCt =>
        {
            var lot = await lots.GetByIdLockedAsync(accountId, command.InventoryLotId, transactionCt);
            if (lot is null || lot.InventoryItemId != item.Id)
            {
                outcome = Result.Failure<Guid>(Error.NotFound(nameof(InventoryLot), command.InventoryLotId));
                return false;
            }

            // A correction can't predate the stock it corrects — that would
            // fabricate impossible historical balances.
            if (command.Date < lot.ReceivedDate)
            {
                outcome = Result.Failure<Guid>(Error.Validation(
                    "InventoryMovement.BeforeLotReceived",
                    $"Adjustment date precedes the lot's received date ({lot.ReceivedDate:yyyy-MM-dd})."));
                return false;
            }

            var adjust = lot.Adjust(command.QuantityDelta);
            if (adjust.IsFailure)
            {
                outcome = Result.Failure<Guid>(adjust.Error);
                return false;
            }

            var type = command.Type == "Discard"
                ? InventoryMovementType.Discard
                : InventoryMovementType.Adjustment;
            var movement = InventoryMovement.Create(
                accountId, item.Id, lot.Id, command.Date, type,
                command.QuantityDelta, item.Unit, clock.UtcNow,
                flockId: null, note: command.Reason);
            await movements.AddAsync(movement, transactionCt);

            // Same transaction as the change (#93).
            await audit.WriteAsync("InventoryItem.Adjust", nameof(InventoryItem), item.Id,
                command.Reason, new { command.Type, command.QuantityDelta }, transactionCt);

            outcome = Result.Success(movement.Id);
            return true;
        }, ct);

        return outcome!;
    }
}
