namespace Cluckwork.Application.Features.Inventory.RecordPurchase;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Accounts;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Inventory;

// Receiving stock (spec §12.2): creates the lot AND its Purchase ledger row in
// one SaveChanges — atomic without an explicit transaction.
public sealed class RecordPurchaseHandler(
    IInventoryItemRepository items,
    IInventoryLotRepository lots,
    IInventoryMovementRepository movements,
    IAccountRepository accounts,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<Result<Guid>> HandleAsync(
        RecordPurchaseCommand command, Guid accountId, CancellationToken ct)
    {
        var item = await items.GetByIdAsync(command.InventoryItemId, ct);
        if (item is null)
            return Result.Failure<Guid>(Error.NotFound(nameof(InventoryItem), command.InventoryItemId));

        if (!item.Active)
            return Result.Failure<Guid>(Error.Domain(
                "InventoryItem.NotActive", "Stock cannot be received for an inactive item."));

        // Backdated receipts are normal (paper catch-up); future ones are not.
        if (command.ReceivedDate > clock.TodayUtc)
            return Result.Failure<Guid>(Error.Validation(
                "InventoryLot.FutureDate", "Received date cannot be in the future."));

        Money unitCost;
        if (command.UnitCostMinorUnits is not null)
        {
            var account = await accounts.GetCurrentAsync(ct);
            if (account is null)
                return Result.Failure<Guid>(Error.NotFound("Account", accountId));
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
            return Result.Failure<Guid>(Error.Validation(
                "InventoryLot.CostRequired",
                "A unit cost is required (the item has no default cost to fall back to)."));
        }

        var lot = InventoryLot.Create(
            Guid.NewGuid(), accountId, item.Id, command.ReceivedDate,
            command.Quantity, unitCost, command.LotNumber, command.ExpiryDate);

        var movement = InventoryMovement.Create(
            accountId, item.Id, lot.Id, command.ReceivedDate,
            InventoryMovementType.Purchase, command.Quantity, item.Unit,
            flockId: null, note: command.Note);

        await lots.AddAsync(lot, ct);
        await movements.AddAsync(movement, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success(lot.Id);
    }
}
