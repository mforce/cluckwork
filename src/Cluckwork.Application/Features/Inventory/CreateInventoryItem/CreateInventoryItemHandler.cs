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

        var defaultCost = await ToMoneyAsync(command.DefaultUnitCostMinorUnits, accountId, ct);
        if (defaultCost.IsFailure)
            return Result.Failure<Guid>(defaultCost.Error);

        var category = Enum.Parse<InventoryCategory>(command.Category, ignoreCase: true);
        var item = InventoryItem.Create(
            Guid.NewGuid(), accountId, farmId,
            command.Name, category, command.Unit, defaultCost.Value);

        await items.AddAsync(item, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success(item.Id);
    }

    // Costs snapshot the account currency like sales orders do — the account's
    // default currency is the single-farm MVP stand-in for the farm's.
    private async Task<Result<Money?>> ToMoneyAsync(
        long? minorUnits, Guid accountId, CancellationToken ct)
    {
        if (minorUnits is null) return Result.Success<Money?>(null);

        var account = await accounts.GetCurrentAsync(ct);
        if (account is null)
            return Result.Failure<Money?>(Error.NotFound("Account", accountId));

        return Result.Success<Money?>(new Money(
            minorUnits.Value, account.DefaultCurrencyCode, account.DefaultCurrencyMinorUnit));
    }
}
