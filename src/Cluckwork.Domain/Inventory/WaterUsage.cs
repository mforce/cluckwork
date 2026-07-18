namespace Cluckwork.Domain.Inventory;

// Water consumed by a flock on a day (spec §12.5). Unlike feed there is no
// lot/ledger behind water — recording it affects nothing downstream — so the
// record itself is EDITABLE (Update + Version token) rather than corrected via
// compensating rows. Corrections become admin-gated with #73.
public sealed class WaterUsage : AggregateRoot<Guid>
{
    public const int MaxNoteLength = 500;
    public static readonly string[] AllowedUnits = ["L", "gal"];

    public Guid FlockId { get; private set; }
    public DateOnly Date { get; private set; }

    // Direct quantity, or derived (MeterEnd − MeterStart) when meters are
    // provided — the handlers guarantee consistency before construction.
    public decimal Quantity { get; private set; }
    public string Unit { get; private set; } = string.Empty;
    public WaterSource Source { get; private set; }
    public decimal? MeterStart { get; private set; }
    public decimal? MeterEnd { get; private set; }
    public string? Note { get; private set; }

    // Reserved for daily-entry integration (spec: daily_entry_id nullable).
    public Guid? DailyEntryId { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }
    public int Version { get; private set; }

    private WaterUsage() { }

    public static WaterUsage Create(
        Guid id, Guid accountId, Guid flockId, DateOnly date,
        decimal quantity, string unit, WaterSource source,
        decimal? meterStart, decimal? meterEnd, DateTime createdAtUtc,
        string? note = null, Guid? dailyEntryId = null)
    {
        var guard = Validate(flockId, quantity, unit, meterStart, meterEnd, note);
        if (guard is not null) throw guard;

        return new WaterUsage
        {
            Id = id, AccountId = accountId,
            FlockId = flockId, Date = date,
            Quantity = quantity, Unit = unit,
            Source = source,
            MeterStart = meterStart, MeterEnd = meterEnd,
            CreatedAtUtc = createdAtUtc,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            DailyEntryId = dailyEntryId
        };
    }

    // Flock and date are identity-like and stay fixed — a wrong flock/date is
    // a delete-and-rerecord... which doesn't exist, so it's a re-record under
    // the right flock/date plus an update zeroing nothing: keep them immutable
    // to make "what did flock X drink on day D" queries stable.
    public Result Update(
        decimal quantity, string unit, WaterSource source,
        decimal? meterStart, decimal? meterEnd, string? note)
    {
        var guard = Validate(FlockId, quantity, unit, meterStart, meterEnd, note);
        if (guard is not null)
            return Result.Failure(Error.Validation("WaterUsage.Invalid", guard.Message));

        Quantity = quantity;
        Unit = unit;
        Source = source;
        MeterStart = meterStart;
        MeterEnd = meterEnd;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        Version++;
        return Result.Success();
    }

    private static Exception? Validate(
        Guid flockId, decimal quantity, string unit,
        decimal? meterStart, decimal? meterEnd, string? note)
    {
        if (flockId == Guid.Empty)
            return new ArgumentException("Flock id is required.", nameof(flockId));
        if (quantity <= 0)
            return new ArgumentOutOfRangeException(nameof(quantity), "Water quantity must be positive.");
        if (!AllowedUnits.Contains(unit))
            return new ArgumentException($"Unit must be one of: {string.Join(", ", AllowedUnits)}.", nameof(unit));
        if ((meterStart is null) != (meterEnd is null))
            return new ArgumentException("Meter start and end must be provided together.", nameof(meterEnd));
        if (meterStart is not null && (meterStart < 0 || meterEnd < meterStart))
            return new ArgumentException("Meter end must be at or after meter start (both non-negative).", nameof(meterEnd));
        if (meterStart is not null && meterEnd - meterStart != quantity)
            return new ArgumentException("Quantity must equal the meter delta.", nameof(quantity));
        if (note is not null && note.Trim().Length > MaxNoteLength)
            return new ArgumentException($"Note cannot exceed {MaxNoteLength} characters.", nameof(note));
        return null;
    }
}

public enum WaterSource { Well, Municipal, Tank, Other }
