namespace Cluckwork.Application.Features.Inventory.CreateInventoryItem;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Accounts;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Inventory;

public sealed class CreateInventoryItemHandler(
    IInventoryItemRepository items,
    IAccountRepository accounts,
    IUnitOfWork unitOfWork)
{
    public async Task<Result<Guid>> HandleAsync(
        CreateInventoryItemCommand command, Guid accountId, CancellationToken ct)
    {
        // Single-farm MVP: items attach to the seeded farm (same convention as
        // grades/flocks). Multi-farm picks up a FarmId parameter here.
        var farmId = SeedDefaults.FarmId;

        // Friendly pre-check; the unique index on (account, farm, lower(name))
        // is the real guarantee and races surface as the global 409 mapping.
        if (await items.NameExistsAsync(farmId, command.Name, excludeId: null, ct))
            return Result.Failure<Guid>(Error.Conflict(
                "InventoryItem.DuplicateName", $"An item named '{command.Name.Trim()}' already exists."));

        // The cost snapshot and the insert share a transaction (#162): a
        // priced item binds the farm currency, so the read below takes the
        // shared lock. An unpriced item reads no currency and takes no lock.
        Result<Guid>? outcome = null;
        await unitOfWork.ExecuteInTransactionAsync(async transactionCt =>
        {
            var defaultCost = await ToMoneyAsync(command.DefaultUnitCostMinorUnits, accountId, transactionCt);
            if (defaultCost.IsFailure)
            {
                outcome = Result.Failure<Guid>(defaultCost.Error);
                return false;
            }

            var category = Enum.Parse<InventoryCategory>(command.Category, ignoreCase: true);
            var item = InventoryItem.Create(
                Guid.NewGuid(), accountId, farmId,
                command.Name, category, command.Unit, defaultCost.Value);

            await items.AddAsync(item, transactionCt);
            outcome = Result.Success(item.Id);
            return true;
        }, ct);

        return outcome!;
    }

    // Costs snapshot the account currency like sales orders do — the account's
    // default currency is the single-farm MVP stand-in for the farm's. FOR
    // SHARE on the account row (#162); only ever called inside the
    // transaction above.
    private async Task<Result<Money?>> ToMoneyAsync(
        long? minorUnits, Guid accountId, CancellationToken ct)
    {
        if (minorUnits is null) return Result.Success<Money?>(null);

        var account = await accounts.GetCurrentSharedLockedAsync(ct);
        if (account is null)
            return Result.Failure<Money?>(Error.NotFound("Account", accountId));

        return Result.Success<Money?>(new Money(
            minorUnits.Value, account.DefaultCurrencyCode, account.DefaultCurrencyMinorUnit));
    }
}
