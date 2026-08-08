namespace Cluckwork.Application.Features.EggLots.RecordEggLotMovement;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Eggs;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Eggs;
using Microsoft.Extensions.Logging;

// Standalone stock write-off / reconciliation (#406): removes lost stock
// (breakage, spoilage, internal use) or applies a recount via a compensating
// ledger row — production is never restated; that is the daily entry's fact.
// The lot is taken FOR UPDATE so corrections serialize against concurrent
// sales draining the same lot, exactly like the allocation path.
public sealed class RecordEggLotMovementHandler(
    IEggLotRepository lots,
    IEggInventoryMovementRepository movements,
    IUnitOfWork unitOfWork,
    IClock clock,
    IAuditWriter audit,
    ILogger<RecordEggLotMovementHandler> logger)
{
    public async Task<Result<RecordEggLotMovementResult>> HandleAsync(
        RecordEggLotMovementCommand command, Guid accountId, CancellationToken ct)
    {
        // The validator enforces this too; re-checked here so the handler can
        // never write a ledger-only type (Production/Sale/Void/...) if reached
        // through a future second caller.
        if (!Enum.TryParse<EggMovementType>(command.MovementType, ignoreCase: false, out var type)
            || type is not (EggMovementType.Discard or EggMovementType.InternalUse or EggMovementType.Reconciliation))
            return Result.Failure<RecordEggLotMovementResult>(Error.Validation(
                "EggLotMovement.MovementType.Allowed",
                "Type must be 'Discard', 'InternalUse' or 'Reconciliation'.")).LogFailure(logger, "RecordEggLotMovement");

        Result<RecordEggLotMovementResult>? outcome = null;

        await unitOfWork.ExecuteInTransactionAsync(async transactionCt =>
        {
            var lot = (await lots.GetByIdsLockedAsync(accountId, [command.EggLotId], transactionCt))
                .SingleOrDefault();
            if (lot is null)
            {
                outcome = Result.Failure<RecordEggLotMovementResult>(
                    Error.NotFound(nameof(EggLot), command.EggLotId));
                return false;
            }

            // A recount may only restore what write-offs removed. The domain's
            // produced ceiling cannot enforce this: Allocate lowers Available
            // without touching Produced, so allocation reads as headroom there
            // — a positive delta into that gap would offer committed eggs for
            // sale twice and make voiding the order impossible (Restore would
            // then exceed produced). Summed under the same lot lock every
            // ledger writer takes, so the figure cannot move underneath us.
            if (command.QuantityDelta > 0)
            {
                var ledger = await movements.ListByLotAsync(lot.Id, transactionCt);
                var writtenOff = -ledger
                    .Where(m => m.MovementType is EggMovementType.Discard
                        or EggMovementType.InternalUse or EggMovementType.Reconciliation)
                    .Sum(m => (long)m.QuantityDelta);
                if (command.QuantityDelta > writtenOff)
                {
                    outcome = Result.Failure<RecordEggLotMovementResult>(Error.Domain(
                        "EggLot.ReconcileExceedsWrittenOff",
                        $"Only {writtenOff} eggs have been written off from this lot; a recount above that is a daily-entry adjustment or a sale void."));
                    return false;
                }
            }

            var adjust = lot.AdjustAvailable(command.QuantityDelta);
            if (adjust.IsFailure)
            {
                outcome = Result.Failure<RecordEggLotMovementResult>(adjust.Error);
                return false;
            }

            var movement = EggInventoryMovement.Create(
                Guid.NewGuid(), accountId, lot.Id, type, command.QuantityDelta,
                nameof(EggLot), lot.Id, clock.UtcNow, command.Reason);
            await movements.AddAsync(movement, transactionCt);

            // Same transaction as the change (#93).
            await audit.WriteAsync(AuditActions.EggLotMovement, nameof(EggLot), lot.Id,
                command.Reason, new { command.MovementType, command.QuantityDelta }, transactionCt);

            outcome = Result.Success(new RecordEggLotMovementResult(
                movement.Id, lot.Id, movement.MovementType.ToString(), movement.QuantityDelta,
                movement.Reason, movement.CreatedAtUtc, lot.QuantityAvailable, lot.Version));
            return true;
        }, ct);

        if (outcome!.IsSuccess)
            logger.LogInformation(
                "Egg lot movement {EggInventoryMovementId} recorded: {QuantityDelta} on lot {EggLotId} ({MovementType})",
                outcome.Value.MovementId, command.QuantityDelta, command.EggLotId, command.MovementType);
        return outcome.LogFailure(logger, "RecordEggLotMovement");
    }
}

// The movement as written plus the lot's post-movement balance, so the SPA
// can show the resulting stock without a second read.
public sealed record RecordEggLotMovementResult(
    Guid MovementId,
    Guid EggLotId,
    string MovementType,
    int QuantityDelta,
    string? Reason,
    DateTimeOffset CreatedAtUtc,
    int QuantityAvailable,
    int Version);
