namespace Cluckwork.Domain.Eggs;

// Egg grade lookup (spec §9.1) — per-account grading buckets. A grade can be a
// size (Large), a quality (Cracked), or a custom bucket (Internal Use); which
// axis defines a bucket is the farm's choice. Daily-entry grade lines reference
// these rows and are restricted to saleable grades — non-saleable buckets are
// captured by the daily entry's cracked/dirty/discarded counts instead.
public sealed class EggGrade : AggregateRoot<Guid>
{
    public const int MaxNameLength = 50;

    public Guid FarmId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public EggGradeType GradeType { get; private set; }
    public int SortOrder { get; private set; }
    public bool IsSaleable { get; private set; }
    public bool Active { get; private set; }

    // #396 — which Daily Entry counter, if any, feeds this grade. The binding
    // is an IDENTITY, deliberately not a name match: a farm may rename
    // "Cracked" to "Segunda" through the ordinary grade endpoint, and a
    // name-matching resolver would then quietly detach the farm's own counter
    // from the farm's own grade. At most one Cracked and one Dirty per farm,
    // enforced by a partial unique index (see InitialCreate).
    public DailyEntryKind DailyEntryKind { get; private set; }

    public int Version { get; private set; }

    private EggGrade() { }

    public static EggGrade Create(
        Guid id, Guid accountId, Guid farmId, string name,
        EggGradeType gradeType, int sortOrder, bool isSaleable,
        DailyEntryKind dailyEntryKind = DailyEntryKind.Manual)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Grade name is required.", nameof(name));
        if (name.Trim().Length > MaxNameLength)
            throw new ArgumentException($"Grade name cannot exceed {MaxNameLength} characters.", nameof(name));

        return new EggGrade
        {
            Id = id, AccountId = accountId, FarmId = farmId,
            Name = name.Trim(),
            GradeType = gradeType,
            SortOrder = sortOrder,
            IsSaleable = isSaleable,
            DailyEntryKind = dailyEntryKind,
            Active = true
        };
    }

    public static IReadOnlyList<EggGrade> Defaults(Guid accountId, Guid farmId) =>
    [
        Create(Guid.NewGuid(), accountId, farmId, "Small", EggGradeType.Size, 0, true),
        Create(Guid.NewGuid(), accountId, farmId, "Medium", EggGradeType.Size, 1, true),
        Create(Guid.NewGuid(), accountId, farmId, "Large", EggGradeType.Size, 2, true),
        Create(Guid.NewGuid(), accountId, farmId, "Jumbo", EggGradeType.Size, 3, true),
        Create(Guid.NewGuid(), accountId, farmId, "Seconds", EggGradeType.Quality, 4, true),
        Create(Guid.NewGuid(), accountId, farmId, "Cracked", EggGradeType.Quality, 5, true, DailyEntryKind.Cracked),
        Create(Guid.NewGuid(), accountId, farmId, "Dirty", EggGradeType.Quality, 6, true, DailyEntryKind.Dirty),
        Create(Guid.NewGuid(), accountId, farmId, "Soft Shell", EggGradeType.Quality, 7, false),
        Create(Guid.NewGuid(), accountId, farmId, "Discarded", EggGradeType.Custom, 8, false),
        Create(Guid.NewGuid(), accountId, farmId, "Internal Use", EggGradeType.Custom, 9, false),
    ];

    // GradeType stays immutable after creation — history recorded under a bucket
    // keeps the axis it was captured with; relabeling the axis would silently
    // reinterpret past entries. DailyEntryKind is immutable for a STRONGER
    // reason (#396): an official entry snapshots the grade id its Cracked or
    // Dirty counter resolved to, so re-pointing a kind afterwards would change
    // what a past day's counter is understood to have produced. Neither is a
    // parameter here, which is the enforcement — there is no path to change one.
    public Result Update(string name, int sortOrder, bool isSaleable)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure(Error.Validation("EggGrade.NameRequired", "Grade name is required."));
        if (name.Trim().Length > MaxNameLength)
            return Result.Failure(Error.Validation(
                "EggGrade.NameTooLong", $"Grade name cannot exceed {MaxNameLength} characters."));

        Name = name.Trim();
        SortOrder = sortOrder;
        IsSaleable = isSaleable;
        Version++;
        return Result.Success();
    }

    public Result Deactivate()
    {
        if (!Active)
            return Result.Failure(Error.Domain("EggGrade.NotActive", "Grade is already inactive."));
        Active = false;
        Version++;
        return Result.Success();
    }

    public Result Activate()
    {
        if (Active)
            return Result.Failure(Error.Domain("EggGrade.AlreadyActive", "Grade is already active."));
        Active = true;
        Version++;
        return Result.Success();
    }
}

public enum EggGradeType { Size, Quality, Custom }

// #396 — which Daily Entry input feeds a grade. `Manual` is every ordinary
// grade, entered by hand in the Grading pane. `Cracked` and `Dirty` are the two
// grades fed by the entry's own condition counters instead, and are excluded
// from manual grading precisely so a condition egg cannot also be counted as a
// manual line (which would produce two lots for one grade on one day).
//
// Deliberately separate from EggGradeType: that says what KIND of bucket this
// is (a size, a quality, a custom one) and is the farm's own taxonomy, while
// this says WHERE THE NUMBER COMES FROM and is the app's wiring. A farm can
// have many Quality grades; only one of them can be the Cracked counter's.
public enum DailyEntryKind { Manual, Cracked, Dirty }
