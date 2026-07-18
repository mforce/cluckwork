namespace Cluckwork.Domain.Sales;

// Lot-level provenance of a FIFO allocation (#60): which egg lots supplied
// which order line, and how much. Written inside the confirm transaction and
// kept forever — the traceability chain (spec §9.6: sale → lots → flock →
// production dates) must survive a void. Voiding marks the rows released
// (stock returned to the source lots) instead of deleting them. A Confirmed
// order therefore always has pending rows (orders confirmed before this table
// existed are the one exception; those cannot be voided).
public sealed class SalesOrderAllocation : Entity<Guid>
{
    public Guid SalesOrderId { get; private set; }
    public Guid SalesOrderItemId { get; private set; }
    public Guid EggLotId { get; private set; }
    public int Quantity { get; private set; }

    // Set when a void returned this quantity to the source lot. Null = the
    // allocation is live (the order holds this stock).
    public DateTime? ReleasedOnUtc { get; private set; }

    private SalesOrderAllocation() { }

    public void MarkReleased(DateTime utcNow) => ReleasedOnUtc = utcNow;

    public static SalesOrderAllocation Create(
        Guid accountId, Guid salesOrderId, Guid salesOrderItemId, Guid eggLotId, int quantity)
    {
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Allocation quantity must be positive.");

        return new SalesOrderAllocation
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            SalesOrderId = salesOrderId,
            SalesOrderItemId = salesOrderItemId,
            EggLotId = eggLotId,
            Quantity = quantity
        };
    }
}
