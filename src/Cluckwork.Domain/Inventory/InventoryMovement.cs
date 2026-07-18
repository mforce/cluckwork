namespace Cluckwork.Domain.Inventory;

// Append-only audit ledger of inventory changes (spec §12.3) — the BirdMovement
// pattern: rows are only ever created, never edited or deleted (the repository
// seam rejects Update/Remove). Mistakes are corrected by a compensating
// Adjustment row. Lot QuantityAvailable is the balance; these rows are the
// explanation of how it got there.
public sealed class InventoryMovement : AggregateRoot<Guid>
{
    public const int MaxNoteLength = 500;

    public Guid InventoryItemId { get; private set; }
    public Guid? InventoryLotId { get; private set; }
    public DateOnly Date { get; private set; }
    public InventoryMovementType Type { get; private set; }

    // Signed: purchases positive, usage/discard negative, adjustment either.
    public decimal QuantityDelta { get; private set; }

    // Snapshot of the item's unit at write time — item unit edits (possible
    // while lot-less) must not reinterpret history.
    public string Unit { get; private set; } = string.Empty;

    public Guid? FlockId { get; private set; }
    public string? Note { get; private set; }

    // When the row was appended (as opposed to Date, the backdatable operational
    // day) — same-day rows order by this, keeping ledger chronology honest.
    // CreatedBy joins with the audit-log slice; single login today.
    public DateTime CreatedAtUtc { get; private set; }

    // What generated this row (spec §12.3 reference_type/reference_id):
    // "FeedUsage" + the usage id for usage rows, so several same-day feedings
    // of the same flock reconcile to their own source records. Null for
    // purchases (the lot id is the reference) and manual adjustments.
    public string? ReferenceType { get; private set; }
    public Guid? ReferenceId { get; private set; }

    private InventoryMovement() { }

    public static InventoryMovement Create(
        Guid accountId, Guid inventoryItemId, Guid? inventoryLotId,
        DateOnly date, InventoryMovementType type, decimal quantityDelta,
        string unit, DateTime createdAtUtc, Guid? flockId = null, string? note = null,
        string? referenceType = null, Guid? referenceId = null)
    {
        if ((referenceType is null) != (referenceId is null))
            throw new ArgumentException("Reference type and id must be set together.", nameof(referenceId));
        if (inventoryItemId == Guid.Empty)
            throw new ArgumentException("Inventory item id is required.", nameof(inventoryItemId));
        if (quantityDelta == 0)
            throw new ArgumentOutOfRangeException(nameof(quantityDelta), "Movement quantity cannot be zero.");
        if (type == InventoryMovementType.Purchase && quantityDelta < 0)
            throw new ArgumentOutOfRangeException(nameof(quantityDelta), "Purchases must be positive.");
        if (type is InventoryMovementType.Usage or InventoryMovementType.Discard && quantityDelta > 0)
            throw new ArgumentOutOfRangeException(nameof(quantityDelta), $"{type} movements must be negative.");
        if (string.IsNullOrWhiteSpace(unit))
            throw new ArgumentException("Unit is required.", nameof(unit));
        if (note is not null && note.Trim().Length > MaxNoteLength)
            throw new ArgumentException($"Note cannot exceed {MaxNoteLength} characters.", nameof(note));

        return new InventoryMovement
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            InventoryItemId = inventoryItemId,
            InventoryLotId = inventoryLotId,
            Date = date,
            Type = type,
            QuantityDelta = quantityDelta,
            Unit = unit,
            CreatedAtUtc = createdAtUtc,
            ReferenceType = referenceType,
            ReferenceId = referenceId,
            FlockId = flockId,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()
        };
    }
}

// Spec §12.3 subset for Phase 1.1; transfer/reconciliation/void arrive with
// their features.
public enum InventoryMovementType { Purchase, Usage, Adjustment, Discard }
