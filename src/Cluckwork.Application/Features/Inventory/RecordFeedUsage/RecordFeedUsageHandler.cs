namespace Cluckwork.Application.Features.Inventory.RecordFeedUsage;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Flocks;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Inventory;

// Feeding a flock (spec §12.4): consumes the item's lots FIFO under the
// canonical (ReceivedDate, Id) FOR UPDATE lock, appends one Usage ledger row
// per lot drained (lot-level provenance — the #60 lesson), and stores the
// usage record with its lot-cost estimate. All in one transaction.
public sealed class RecordFeedUsageHandler(
    IInventoryItemRepository items,
    IInventoryLotRepository lots,
    IInventoryMovementRepository movements,
    IFeedUsageRepository usages,
    IFlockRepository flocks,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<Result<RecordFeedUsageResponse>> HandleAsync(
        RecordFeedUsageCommand command, Guid accountId, CancellationToken ct)
    {
        // Tenant query filters scope both lookups — foreign rows read as null.
        var flock = await flocks.GetByIdAsync(command.FlockId, ct);
        if (flock is null)
            return Result.Failure<RecordFeedUsageResponse>(
                Error.NotFound("Flock", command.FlockId));

        // Same lifecycle rule as production records: depleted allows backfill
        // up to its depletion date, archived never.
        if (!flock.CanRecordProductionOn(command.Date))
            return Result.Failure<RecordFeedUsageResponse>(Error.Validation(
                "FeedUsage.FlockNotActive",
                $"Flock '{flock.Name}' is {flock.Status.ToString().ToLowerInvariant()} — feed cannot be recorded for this date."));

        if (command.Date > clock.TodayUtc)
            return Result.Failure<RecordFeedUsageResponse>(Error.Validation(
                "FeedUsage.FutureDate", "Usage date cannot be in the future."));

        var item = await items.GetByIdAsync(command.InventoryItemId, ct);
        if (item is null)
            return Result.Failure<RecordFeedUsageResponse>(
                Error.NotFound(nameof(InventoryItem), command.InventoryItemId));
        // Inactive items can still be used up — deactivation only stops NEW
        // stock from arriving; existing feed gets eaten either way.

        Result<RecordFeedUsageResponse>? outcome = null;

        await unitOfWork.ExecuteInTransactionAsync(async transactionCt =>
        {
            var lockedLots = await lots.GetAvailableFifoLockedAsync(
                accountId, item.Id, transactionCt);

            var available = lockedLots.Sum(l => l.QuantityAvailable);
            if (available < command.Quantity)
            {
                outcome = Result.Failure<RecordFeedUsageResponse>(Error.Domain(
                    "InventoryLot.InsufficientStock",
                    $"Requested {command.Quantity} {item.Unit} but only {available} {item.Unit} in stock."));
                return false;
            }

            var createdAt = clock.UtcNow;
            var usageId = Guid.NewGuid();
            var remaining = command.Quantity;
            long costMinorUnits = 0;
            string? currencyCode = null;
            var currencyMinorUnit = 0;

            foreach (var lot in lockedLots)
            {
                if (remaining <= 0) break;
                var take = Math.Min(remaining, lot.QuantityAvailable);
                var consume = lot.Consume(take);
                if (consume.IsFailure)
                {
                    outcome = Result.Failure<RecordFeedUsageResponse>(consume.Error);
                    return false;
                }

                // Whole-minor-unit rounding per lot; the estimate is a KPI
                // input (spec §19), not an invoice.
                costMinorUnits += (long)Math.Round(take * lot.UnitCost.MinorUnits, MidpointRounding.AwayFromZero);
                currencyCode = lot.UnitCost.CurrencyCode;
                currencyMinorUnit = lot.UnitCost.CurrencyMinorUnit;

                await movements.AddAsync(InventoryMovement.Create(
                    accountId, item.Id, lot.Id, command.Date,
                    InventoryMovementType.Usage, -take, item.Unit,
                    createdAt, flockId: flock.Id, note: command.Note), transactionCt);

                remaining -= take;
            }

            var estimatedCost = new Money(costMinorUnits, currencyCode!, currencyMinorUnit);
            var usage = FeedUsage.Create(
                usageId, accountId, flock.Id, item.Id,
                command.Date, command.Quantity, item.Unit, estimatedCost, command.Note);
            await usages.AddAsync(usage, transactionCt);

            outcome = Result.Success(new RecordFeedUsageResponse(
                usageId, command.Quantity, costMinorUnits, estimatedCost.CurrencyCode));
            return true;
        }, ct);

        return outcome!;
    }
}
