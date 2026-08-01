namespace Cluckwork.Application.Features.Inventory.UpdateInventoryItem;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Accounts;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Inventory;

public sealed class UpdateInventoryItemHandler(
    IInventoryItemRepository items,
    IAccountRepository accounts,
    IUnitOfWork unitOfWork)
{
    public async Task<Result> HandleAsync(
        UpdateInventoryItemCommand command, Guid accountId, CancellationToken ct)
    {
        Result? outcome = null;

        // The whole check-then-update runs under a FOR UPDATE lock on the item
        // row: a concurrent first purchase (which takes the same lock) can no
        // longer slip a lot in between the HasLots check and the unit change.
        await unitOfWork.ExecuteInTransactionAsync(async transactionCt =>
        {
            var item = await items.GetByIdLockedAsync(accountId, command.InventoryItemId, transactionCt);
            if (item is null || item.AccountId != accountId)
            {
                // Foreign-tenant items read as missing (locked read bypasses
                // the query filter, so the check is explicit here).
                outcome = Result.Failure(Error.NotFound(nameof(InventoryItem), command.InventoryItemId));
                return false;
            }

            if (await items.NameExistsAsync(item.FarmId, command.Name, excludeId: item.Id, transactionCt))
            {
                outcome = Result.Failure(Error.Conflict(
                    "InventoryItem.DuplicateName", $"An item named '{command.Name.Trim()}' already exists."));
                return false;
            }

            // Unit is how every recorded quantity of this item is measured; once
            // lots exist, changing it would silently reinterpret them.
            if (!string.Equals(item.Unit, command.Unit.Trim(), StringComparison.Ordinal)
                && await items.HasLotsAsync(item.Id, transactionCt))
            {
                outcome = Result.Failure(Error.Domain(
                    "InventoryItem.UnitLocked",
                    "The unit cannot change once stock has been received for this item."));
                return false;
            }

            Money? defaultCost = null;
            if (command.DefaultUnitCostMinorUnits is not null)
            {
                // #162 — FOR SHARE: the cost stamped below participates in the
                // currency-lock protocol. Taken AFTER the item row lock; no
                // deadlock cycle exists because the only exclusive taker of the
                // account lock (the settings handler) locks nothing else.
                var account = await accounts.GetCurrentSharedLockedAsync(transactionCt);
                if (account is null)
                {
                    outcome = Result.Failure(Error.NotFound("Account", accountId));
                    return false;
                }
                defaultCost = new Money(
                    command.DefaultUnitCostMinorUnits.Value,
                    account.DefaultCurrencyCode, account.DefaultCurrencyMinorUnit);
            }

            var result = item.Update(command.Name, command.Unit, defaultCost);
            if (result.IsFailure)
            {
                outcome = result;
                return false;
            }

            outcome = Result.Success();
            return true;
        }, ct);

        return outcome!;
    }
}
