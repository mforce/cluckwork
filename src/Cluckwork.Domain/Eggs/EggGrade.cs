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
    public int Version { get; private set; }

    private EggGrade() { }

    public static EggGrade Create(
        Guid id, Guid accountId, Guid farmId, string name,
        EggGradeType gradeType, int sortOrder, bool isSaleable)
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
            Active = true
        };
    }

    // GradeType stays immutable after creation — history recorded under a bucket
    // keeps the axis it was captured with; relabeling the axis would silently
    // reinterpret past entries.
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
