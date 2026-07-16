namespace Cluckwork.Domain.Eggs;

public sealed class DailyEntry : AggregateRoot<Guid>
{
    private readonly List<DailyEntryGrade> _grades = [];

    public Guid FarmId { get; private set; }
    public Guid HouseId { get; private set; }
    public Guid FlockId { get; private set; }
    public DateOnly Date { get; private set; }
    public DailyEntryStatus Status { get; private set; }

    public int TotalEggs { get; private set; }
    public int CrackedEggs { get; private set; }
    public int DirtyEggs { get; private set; }
    public int DiscardedEggs { get; private set; }
    public int MortalityCount { get; private set; }

    // Sellable production by grade (Phase 1: eggs are graded at collection).
    public IReadOnlyList<DailyEntryGrade> Grades => _grades.AsReadOnly();

    // Optimistic concurrency token — functional spec §10.9.1
    public int Version { get; private set; }

    private DailyEntry() { }

    // Unique constraint: (account_id, farm_id, house_id, flock_id, date)
    public static DailyEntry Create(
        Guid id, Guid accountId, Guid farmId, Guid houseId, Guid flockId, DateOnly date)
    {
        return new DailyEntry
        {
            Id = id, AccountId = accountId,
            FarmId = farmId, HouseId = houseId,
            FlockId = flockId, Date = date,
            Status = DailyEntryStatus.Draft
        };
    }

    public Result RecordProduction(
        int totalEggs, int cracked, int dirty, int discarded, int mortality,
        IReadOnlyCollection<GradeQuantity>? grades = null)
    {
        if (Status is DailyEntryStatus.Locked or DailyEntryStatus.Voided)
            return Result.Failure(Error.Domain(
                "DailyEntry.Immutable", "Cannot modify a locked or voided entry."));

        // null = leave existing grade lines untouched (older clients omit the
        // field); the kept lines must still fit the new totals. [] clears.
        var effectiveGrades = grades ?? CurrentGradeQuantities();
        var gradeResult = ValidateGrades(totalEggs, cracked, dirty, discarded, effectiveGrades);
        if (gradeResult.IsFailure) return gradeResult;

        TotalEggs = totalEggs;
        CrackedEggs = cracked;
        DirtyEggs = dirty;
        DiscardedEggs = discarded;
        MortalityCount = mortality;
        if (grades is not null)
            ReplaceGrades(grades);
        Version++;
        return Result.Success();
    }

    public Result Submit()
    {
        if (Status != DailyEntryStatus.Draft)
            return Result.Failure(Error.Domain(
                "DailyEntry.NotDraft", "Only draft entries can be submitted."));
        Status = DailyEntryStatus.Submitted;
        return Result.Success();
    }

    public Result Lock()
    {
        if (Status != DailyEntryStatus.Submitted)
            return Result.Failure(Error.Domain(
                "DailyEntry.NotSubmitted", "Only submitted entries can be locked."));
        Status = DailyEntryStatus.Locked;
        return Result.Success();
    }

    public Result ManagerAdjust(
        int totalEggs, int cracked, int dirty, int discarded, int mortality, string reason,
        IReadOnlyCollection<GradeQuantity>? grades = null)
    {
        if (Status != DailyEntryStatus.Locked)
            return Result.Failure(Error.Domain(
                "DailyEntry.NotLocked", "Manager adjustments require a locked entry."));

        var effectiveGrades = grades ?? CurrentGradeQuantities();
        var gradeResult = ValidateGrades(totalEggs, cracked, dirty, discarded, effectiveGrades);
        if (gradeResult.IsFailure) return gradeResult;

        TotalEggs = totalEggs;
        CrackedEggs = cracked;
        DirtyEggs = dirty;
        DiscardedEggs = discarded;
        MortalityCount = mortality;
        if (grades is not null)
            ReplaceGrades(grades);
        Status = DailyEntryStatus.ManagerAdjusted;
        Version++;
        return Result.Success();
    }

    public const int MaxGradeCodeLength = 20;

    private List<GradeQuantity> CurrentGradeQuantities() =>
        _grades.Select(l => new GradeQuantity(l.GradeCode, l.Quantity)).ToList();

    private static Result ValidateGrades(
        int totalEggs, int cracked, int dirty, int discarded,
        IReadOnlyCollection<GradeQuantity> grades)
    {
        if (grades.Count == 0) return Result.Success();

        if (grades.Any(g => string.IsNullOrWhiteSpace(g.GradeCode)))
            return Result.Failure(Error.Validation(
                "DailyEntry.InvalidGrade", "Grade code is required."));

        if (grades.Any(g => g.GradeCode.Trim().Length > MaxGradeCodeLength))
            return Result.Failure(Error.Validation(
                "DailyEntry.InvalidGrade", $"Grade code cannot exceed {MaxGradeCodeLength} characters."));

        if (grades.Any(g => g.Quantity <= 0))
            return Result.Failure(Error.Validation(
                "DailyEntry.InvalidGrade", "Grade quantities must be positive."));

        if (grades.Select(g => g.GradeCode).Distinct(StringComparer.OrdinalIgnoreCase).Count() != grades.Count)
            return Result.Failure(Error.Validation(
                "DailyEntry.DuplicateGrade", "Each grade may appear only once."));

        // Grades are the sellable portion; cracked/dirty/discarded are losses out
        // of the same total. long accumulation — Sum<int> would throw on overflow.
        var sellable = (long)totalEggs - cracked - dirty - discarded;
        if (grades.Sum(g => (long)g.Quantity) > sellable)
            return Result.Failure(Error.Domain(
                "DailyEntry.GradesExceedTotal",
                "Graded quantities cannot exceed total eggs minus cracked/dirty/discarded."));

        return Result.Success();
    }

    // Full replace: the daily entry is the single source for the day's grading, so
    // a re-record supersedes previous lines. Reconciled in place (update matching
    // codes, remove gone, add new) rather than clear+add — deleting and re-inserting
    // the same (entry, grade) key in one save can trip the unique index depending on
    // EF's statement ordering.
    private void ReplaceGrades(IReadOnlyCollection<GradeQuantity> grades)
    {
        _grades.RemoveAll(line => !grades.Any(g =>
            string.Equals(g.GradeCode.Trim(), line.GradeCode, StringComparison.OrdinalIgnoreCase)));

        foreach (var g in grades)
        {
            var existing = _grades.FirstOrDefault(line =>
                string.Equals(line.GradeCode, g.GradeCode.Trim(), StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
                existing.UpdateQuantity(g.Quantity);
            else
                _grades.Add(DailyEntryGrade.Create(AccountId, Id, g.GradeCode, g.Quantity));
        }
    }
}

public enum DailyEntryStatus { Draft, Submitted, Locked, ManagerAdjusted, Voided }

// Input value for recording production by grade.
public sealed record GradeQuantity(string GradeCode, int Quantity);

public sealed class DailyEntryGrade : Entity<Guid>
{
    public Guid DailyEntryId { get; private set; }
    public string GradeCode { get; private set; } = string.Empty;
    public int Quantity { get; private set; }

    private DailyEntryGrade() { }

    internal void UpdateQuantity(int quantity) => Quantity = quantity;

    // Id is deliberately left unset (EF generates it on add). A client-set key on
    // a line added to an already-tracked entry would be discovered as Modified,
    // producing an UPDATE for a row that doesn't exist yet.
    internal static DailyEntryGrade Create(
        Guid accountId, Guid dailyEntryId, string gradeCode, int quantity)
    {
        return new DailyEntryGrade
        {
            AccountId = accountId,
            DailyEntryId = dailyEntryId,
            // Normalized: grade codes are identifiers matched across daily entries,
            // egg lots, and sales lines; the DB unique index is case-sensitive, so
            // storage must be canonical ("a-large " and "A-Large" are one grade).
            GradeCode = gradeCode.Trim().ToUpperInvariant(),
            Quantity = quantity
        };
    }
}
