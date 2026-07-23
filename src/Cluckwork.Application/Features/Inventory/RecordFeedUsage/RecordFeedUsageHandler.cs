namespace Cluckwork.Application.Features.Inventory.RecordFeedUsage;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Flocks;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Inventory;

// Feeding a flock (spec §12.4): consumes the item's lots FIFO under the
// canonical (ReceivedDate, Id) FOR UPDATE lock, appends one Usage ledger row
// per lot drained (lot-level provenance — the #60 lesson) referencing the
// usage record, and stores the usage with its lot-cost estimate. The item row
// is locked FIRST (item → lots, the same order the purchase path uses), so a
// concurrent backdated purchase can't slip an older lot in after the FIFO
// read. All in one transaction.
public sealed class RecordFeedUsageHandler(
    IInventoryItemRepository items,
    IInventoryLotRepository lots,
    IInventoryMovementRepository movements,
    IFeedUsageRepository usages,
    IFlockRepository flocks,
    IFlockScopeGuard flockScope,
    IUnitOfWork unitOfWork,
    IClock clock,
    IFarmClock farmClock)
{
    // Categories a flock can plausibly eat. Recording packaging or equipment
    // parts as feed would poison the §19 feed-cost KPIs.
    private static readonly InventoryCategory[] FeedableCategories =
        [InventoryCategory.Feed, InventoryCategory.Supplement, InventoryCategory.Additive];

    public async Task<Result<RecordFeedUsageResponse>> HandleAsync(
        RecordFeedUsageCommand command, Guid accountId, CancellationToken ct)
    {
        if (command.Date > await farmClock.TodayAsync(ct))
            return Result.Failure<RecordFeedUsageResponse>(Error.Validation(
                "FeedUsage.FutureDate", "Usage date cannot be in the future."));

        // Spec §5.3 (#103): scoped workers may only record for assigned flocks.
        var scope = await flockScope.CheckAsync(command.FlockId, ct);
        if (scope.IsFailure) return Result.Failure<RecordFeedUsageResponse>(scope.Error);

        Result<RecordFeedUsageResponse>? outcome = null;

        await unitOfWork.ExecuteInTransactionAsync(async transactionCt =>
        {
            // Item lock first (item → lots, matching the purchase path) —
            // serializes against purchases and unit edits.
            var item = await items.GetByIdLockedAsync(command.InventoryItemId, transactionCt);
            if (item is null || item.AccountId != accountId)
            {
                outcome = Result.Failure<RecordFeedUsageResponse>(
                    Error.NotFound(nameof(InventoryItem), command.InventoryItemId));
                return false;
            }
            // Inactive items can still be used up — deactivation only stops NEW
            // stock from arriving; existing feed gets eaten either way.

            if (!FeedableCategories.Contains(item.Category))
            {
                outcome = Result.Failure<RecordFeedUsageResponse>(Error.Domain(
                    "FeedUsage.NotFeedCategory",
                    $"'{item.Name}' is {item.Category} — only Feed, Supplement, or Additive items can be fed to a flock."));
                return false;
            }

            // Inside the transaction so a concurrent deplete/archive shrinks
            // the race to the commit itself. The flock row isn't locked: a
            // same-day deplete racing a same-day feed is business-valid either
            // way (the birds ate before they left).
            var flock = await flocks.GetByIdAsync(command.FlockId, transactionCt);
            if (flock is null)
            {
                outcome = Result.Failure<RecordFeedUsageResponse>(
                    Error.NotFound("Flock", command.FlockId));
                return false;
            }

            if (!flock.CanRecordProductionOn(command.Date))
            {
                outcome = Result.Failure<RecordFeedUsageResponse>(Error.Validation(
                    "FeedUsage.FlockNotActive",
                    $"Flock '{flock.Name}' is {flock.Status.ToString().ToLowerInvariant()} — feed cannot be recorded for this date."));
                return false;
            }

            // as-of the usage date: a backdated feeding can't consume lots
            // that hadn't been received yet.
            var lockedLots = await lots.GetAvailableFifoLockedAsync(
                accountId, item.Id, command.Date, transactionCt);

            var available = lockedLots.Sum(l => l.QuantityAvailable);
            if (available < command.Quantity)
            {
                outcome = Result.Failure<RecordFeedUsageResponse>(Error.Domain(
                    "InventoryLot.InsufficientStock",
                    $"Requested {command.Quantity} {item.Unit} but only {available} {item.Unit} in stock on {command.Date:yyyy-MM-dd}."));
                return false;
            }

            var createdAt = clock.UtcNow;
            var usageId = Guid.NewGuid();
            var remaining = command.Quantity;
            decimal costMinorUnits = 0;
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
                // input (spec §19), not an invoice. Accumulated in decimal and
                // bounds-checked below — silent long wrap-around is worse than
                // a refused request.
                costMinorUnits += Math.Round(take * lot.UnitCost.MinorUnits, MidpointRounding.AwayFromZero);
                currencyCode = lot.UnitCost.CurrencyCode;
                currencyMinorUnit = lot.UnitCost.CurrencyMinorUnit;

                await movements.AddAsync(InventoryMovement.Create(
                    accountId, item.Id, lot.Id, command.Date,
                    InventoryMovementType.Usage, -take, item.Unit,
                    createdAt, flockId: flock.Id, note: command.Note,
                    referenceType: nameof(FeedUsage), referenceId: usageId), transactionCt);

                remaining -= take;
            }

            if (costMinorUnits > long.MaxValue)
            {
                outcome = Result.Failure<RecordFeedUsageResponse>(Error.Validation(
                    "FeedUsage.CostOutOfRange", "The estimated cost exceeds the supported amount range."));
                return false;
            }

            var estimatedCost = new Money((long)costMinorUnits, currencyCode!, currencyMinorUnit);
            var usage = FeedUsage.Create(
                usageId, accountId, flock.Id, item.Id,
                command.Date, command.Quantity, item.Unit, estimatedCost, createdAt, command.Note);
            await usages.AddAsync(usage, transactionCt);

            outcome = Result.Success(new RecordFeedUsageResponse(
                usageId, command.Quantity, estimatedCost.MinorUnits,
                estimatedCost.CurrencyCode, estimatedCost.CurrencyMinorUnit));
            return true;
        }, ct);

        return outcome!;
    }
}
