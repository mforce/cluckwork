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
        // Tenant query filter scopes the lookup — foreign items read as null.
        var item = await items.GetByIdAsync(command.InventoryItemId, ct);
        if (item is null)
            return Result.Failure(Error.NotFound(nameof(InventoryItem), command.InventoryItemId));

        if (await items.NameExistsAsync(item.FarmId, command.Name, excludeId: item.Id, ct))
            return Result.Failure(Error.Conflict(
                "InventoryItem.DuplicateName", $"An item named '{command.Name.Trim()}' already exists."));

        // Unit is how every recorded quantity of this item is measured; once
        // lots exist, changing it would silently reinterpret them.
        if (!string.Equals(item.Unit, command.Unit.Trim(), StringComparison.Ordinal)
            && await items.HasLotsAsync(item.Id, ct))
            return Result.Failure(Error.Domain(
                "InventoryItem.UnitLocked",
                "The unit cannot change once stock has been received for this item."));

        Money? defaultCost = null;
        if (command.DefaultUnitCostMinorUnits is not null)
        {
            var account = await accounts.GetCurrentAsync(ct);
            if (account is null)
                return Result.Failure(Error.NotFound("Account", accountId));
            defaultCost = new Money(
                command.DefaultUnitCostMinorUnits.Value,
                account.DefaultCurrencyCode, account.DefaultCurrencyMinorUnit);
        }

        var result = item.Update(command.Name, command.Unit, defaultCost);
        if (result.IsFailure)
            return result;

        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}
