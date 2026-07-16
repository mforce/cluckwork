namespace Cluckwork.Domain.Flocks;

// Append-only bird ledger row (#54): every change to a flock's living count is
// a movement — mortality (generated from submitted daily entries), culls, and
// manual adjustments. Rows are never edited; corrections are new Adjustment
// rows with the opposite sign. No Version token needed: immutable after Create.
//
// Quantity is "birds removed" — positive shrinks the flock. Adjustment rows
// may be negative to add birds back (miscount corrections).
public sealed class BirdMovement : AggregateRoot<Guid>
{
    public const int MaxNoteLength = 500;

    public Guid FlockId { get; private set; }
    public DateOnly Date { get; private set; }
    public BirdMovementType Type { get; private set; }
    public int Quantity { get; private set; }
    public string? Note { get; private set; }
    // Set on generated Mortality rows: the daily entry that produced this row,
    // so a future reconciliation flow (manager adjust / void-and-resubmit) can
    // find and correct the ledger side. Null for manual movements.
    public Guid? DailyEntryId { get; private set; }

    private BirdMovement() { }

    public static BirdMovement Create(
        Guid id, Guid accountId, Guid flockId,
        DateOnly date, BirdMovementType type, int quantity, string? note = null,
        Guid? dailyEntryId = null)
    {
        if (quantity == 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Movement quantity cannot be zero.");
        if (quantity < 0 && type != BirdMovementType.Adjustment)
            throw new ArgumentOutOfRangeException(nameof(quantity),
                "Only adjustments may be negative; mortality and culls remove birds.");
        if (date == default)
            throw new ArgumentException("Movement date is required.", nameof(date));

        var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmed?.Length > MaxNoteLength)
            throw new ArgumentException($"Note cannot exceed {MaxNoteLength} characters.", nameof(note));

        return new BirdMovement
        {
            Id = id, AccountId = accountId,
            FlockId = flockId,
            Date = date,
            Type = type,
            Quantity = quantity,
            Note = trimmed,
            DailyEntryId = dailyEntryId,
        };
    }
}

public enum BirdMovementType { Mortality, Cull, Adjustment }
