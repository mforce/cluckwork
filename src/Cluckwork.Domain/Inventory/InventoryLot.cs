namespace Cluckwork.Domain.Inventory;

// A received batch of an inventory item (spec §12.2): purchase-sized, with its
// own cost and optional supplier lot number / expiry. Stock on hand for an
// item = Σ QuantityAvailable across its lots — the lots are the source of
// truth; InventoryMovement rows are the audit ledger derived from them.
public sealed class InventoryLot : AggregateRoot<Guid>
{
    public const int MaxLotNumberLength = 100;

    public Guid InventoryItemId { get; private set; }
    public DateOnly ReceivedDate { get; private set; }
    public string? LotNumber { get; private set; }
    public DateOnly? ExpiryDate { get; private set; }

    // decimal, not int: feed is weighed ("437.5 kg"), unlike counted eggs.
    public decimal QuantityReceived { get; private set; }
    public decimal QuantityAvailable { get; private set; }

    public Money UnitCost { get; private set; } = null!;

    // Row-version token; the usage path consumes under FOR UPDATE like egg-lot
    // allocation (canonical (ReceivedDate, Id) lock order).
    public int Version { get; private set; }

    private InventoryLot() { }

    public static InventoryLot Create(
        Guid id, Guid accountId, Guid inventoryItemId, DateOnly receivedDate,
        decimal quantity, Money unitCost, string? lotNumber, DateOnly? expiryDate)
    {
        if (inventoryItemId == Guid.Empty)
            throw new ArgumentException("Inventory item id is required.", nameof(inventoryItemId));
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Lot quantity must be positive.");
        if (unitCost.IsNegative)
            throw new ArgumentException("Unit cost cannot be negative.", nameof(unitCost));
        if (lotNumber is not null && lotNumber.Trim().Length > MaxLotNumberLength)
            throw new ArgumentException($"Lot number cannot exceed {MaxLotNumberLength} characters.", nameof(lotNumber));
        if (expiryDate is not null && expiryDate < receivedDate)
            throw new ArgumentException("Expiry date cannot precede the received date.", nameof(expiryDate));

        return new InventoryLot
        {
            Id = id, AccountId = accountId,
            InventoryItemId = inventoryItemId,
            ReceivedDate = receivedDate,
            LotNumber = string.IsNullOrWhiteSpace(lotNumber) ? null : lotNumber.Trim(),
            ExpiryDate = expiryDate,
            QuantityReceived = quantity,
            QuantityAvailable = quantity,
            UnitCost = unitCost
        };
    }

    // Call only inside the pessimistic FOR UPDATE transaction (usage path,
    // PR2 of #66) — mirrors EggLot.Allocate.
    public Result Consume(decimal quantity)
    {
        if (quantity <= 0)
            return Result.Failure(Error.Validation(
                "InventoryLot.InvalidQuantity", "Consumed quantity must be positive."));

        if (quantity > QuantityAvailable)
            return Result.Failure(Error.Domain(
                "InventoryLot.InsufficientStock",
                $"Requested {quantity} but only {QuantityAvailable} available."));

        QuantityAvailable -= quantity;
        Version++;
        return Result.Success();
    }

    // Signed correction (#66 part 2): negative writes off / fixes an
    // over-recorded purchase, positive undoes an over-consumption. Available
    // stays within [0, QuantityReceived] — stock genuinely beyond the receipt
    // is a new purchase, not an adjustment. Same FOR UPDATE rule as Consume.
    public Result Adjust(decimal delta)
    {
        if (delta == 0)
            return Result.Failure(Error.Validation(
                "InventoryLot.InvalidQuantity", "Adjustment quantity cannot be zero."));

        if (delta < 0 && -delta > QuantityAvailable)
            return Result.Failure(Error.Domain(
                "InventoryLot.InsufficientStock",
                $"Cannot remove {-delta}: only {QuantityAvailable} available in this lot."));

        if (delta > 0 && QuantityAvailable + delta > QuantityReceived)
            return Result.Failure(Error.Domain(
                "InventoryLot.AdjustExceedsReceived",
                $"Adjusting by {delta} would exceed the {QuantityReceived} received in this lot; record extra stock as a purchase."));

        QuantityAvailable += delta;
        Version++;
        return Result.Success();
    }
}
