namespace Cluckwork.Application.Features.Inventory.RecordPurchase;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Accounts;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Inventory;
using Microsoft.Extensions.Logging;

// Receiving stock (spec §12.2): creates the lot AND its Purchase ledger row
// atomically. Runs under a FOR UPDATE lock on the item row so the unit read
// here can't interleave with a unit edit that saw no lots yet (TOCTOU).
public sealed class RecordPurchaseHandler(
    IInventoryItemRepository items,
    IInventoryLotRepository lots,
    IInventoryMovementRepository movements,
    IAccountRepository accounts,
    IUnitOfWork unitOfWork,
    IClock clock,
    IFarmClock farmClock,
    ILogger<RecordPurchaseHandler> logger)
{
    public async Task<Result<Guid>> HandleAsync(
        RecordPurchaseCommand command, Guid accountId, CancellationToken ct)
    {
        Result<Guid>? outcome = null;

        await unitOfWork.ExecuteInTransactionAsync(async transactionCt =>
        {
            var item = await items.GetByIdLockedAsync(accountId, command.InventoryItemId, transactionCt);
            if (item is null || item.AccountId != accountId)
            {
                outcome = Result.Failure<Guid>(Error.NotFound(nameof(InventoryItem), command.InventoryItemId));
                return false;
            }

            if (!item.Active)
            {
                outcome = Result.Failure<Guid>(Error.Domain(
                    "InventoryItem.NotActive", "Stock cannot be received for an inactive item."));
                return false;
            }

            // Backdated receipts are normal (paper catch-up); future ones are not.
            if (command.ReceivedDate > await farmClock.TodayAsync(ct))
            {
                outcome = Result.Failure<Guid>(Error.Validation(
                    "InventoryLot.FutureDate", "Received date cannot be in the future."));
                return false;
            }

            Money unitCost;
            if (command.UnitCostMinorUnits is not null)
            {
                // #162 — FOR SHARE: the cost stamped below participates in the
                // currency-lock protocol. Taken AFTER the item row lock; no
                // deadlock cycle exists because the only exclusive taker of the
                // account lock (the settings handler) locks nothing else.
                var account = await accounts.GetCurrentSharedLockedAsync(transactionCt);
                if (account is null)
                {
                    outcome = Result.Failure<Guid>(Error.NotFound("Account", accountId));
                    return false;
                }
                unitCost = new Money(
                    command.UnitCostMinorUnits.Value,
                    account.DefaultCurrencyCode, account.DefaultCurrencyMinorUnit);
            }
            else if (item.DefaultUnitCost is not null)
            {
                unitCost = item.DefaultUnitCost;
            }
            else
            {
                outcome = Result.Failure<Guid>(Error.Validation(
                    "InventoryLot.CostRequired",
                    "A unit cost is required (the item has no default cost to fall back to)."));
                return false;
            }

            var lot = InventoryLot.Create(
                Guid.NewGuid(), accountId, item.Id, command.ReceivedDate,
                command.Quantity, unitCost, command.LotNumber, command.ExpiryDate);

            var movement = InventoryMovement.Create(
                accountId, item.Id, lot.Id, command.ReceivedDate,
                InventoryMovementType.Purchase, command.Quantity, item.Unit,
                clock.UtcNow, flockId: null, note: command.Note);

            await lots.AddAsync(lot, transactionCt);
            await movements.AddAsync(movement, transactionCt);
            outcome = Result.Success(lot.Id);
            return true;
        }, ct);

        if (outcome!.IsSuccess)
            logger.LogInformation(
                "Purchase recorded: lot {InventoryLotId} of {Quantity} for item {InventoryItemId} on {ReceivedDate}",
                outcome.Value, command.Quantity, command.InventoryItemId, command.ReceivedDate);
        return outcome.LogFailure(logger, "RecordPurchase");
    }
}
