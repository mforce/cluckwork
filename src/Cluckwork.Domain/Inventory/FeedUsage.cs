namespace Cluckwork.Domain.Inventory;

// A feeding event (spec §12.4): how much of an item a flock consumed on a day.
// Created only — the stock effect lives in the lots/movement ledger, and a
// mis-entered usage is corrected there via a compensating Adjustment (an edit
// here would silently detach the record from the movements it generated).
public sealed class FeedUsage : AggregateRoot<Guid>
{
    public const int MaxNoteLength = 500;

    public Guid FlockId { get; private set; }
    public Guid InventoryItemId { get; private set; }
    public DateOnly Date { get; private set; }
    public decimal Quantity { get; private set; }

    // Snapshot of the item's unit at write time, like movement rows.
    public string Unit { get; private set; } = string.Empty;

    // Σ(consumed-from-lot × that lot's unit cost) — lot_cost costing (spec
    // §12.4); feeds the §19 feed-cost KPIs.
    public Money EstimatedCost { get; private set; } = null!;

    // #446 — the non-voided daily entry that existed for the flock's
    // (farm, house, flock, date) when this row was recorded, or null.
    // Best-effort provenance: never backfilled; flock+date is the join.
    public Guid? DailyEntryId { get; private set; }

    public string? Note { get; private set; }

    // Append timestamp — same-day records order by this, like movement rows.
    public DateTime CreatedAtUtc { get; private set; }

    public int Version { get; private set; }

    private FeedUsage() { }

    public static FeedUsage Create(
        Guid id, Guid accountId, Guid flockId, Guid inventoryItemId,
        DateOnly date, decimal quantity, string unit, Money estimatedCost,
        DateTime createdAtUtc, string? note = null, Guid? dailyEntryId = null)
    {
        if (flockId == Guid.Empty)
            throw new ArgumentException("Flock id is required.", nameof(flockId));
        if (inventoryItemId == Guid.Empty)
            throw new ArgumentException("Inventory item id is required.", nameof(inventoryItemId));
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Usage quantity must be positive.");
        if (string.IsNullOrWhiteSpace(unit))
            throw new ArgumentException("Unit is required.", nameof(unit));
        if (estimatedCost.IsNegative)
            throw new ArgumentException("Estimated cost cannot be negative.", nameof(estimatedCost));
        if (note is not null && note.Trim().Length > MaxNoteLength)
            throw new ArgumentException($"Note cannot exceed {MaxNoteLength} characters.", nameof(note));

        return new FeedUsage
        {
            Id = id, AccountId = accountId,
            FlockId = flockId,
            InventoryItemId = inventoryItemId,
            Date = date,
            Quantity = quantity,
            Unit = unit,
            EstimatedCost = estimatedCost,
            CreatedAtUtc = createdAtUtc,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            DailyEntryId = dailyEntryId
        };
    }
}
