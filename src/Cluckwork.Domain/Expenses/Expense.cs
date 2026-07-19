namespace Cluckwork.Domain.Expenses;

// A single money-out record (spec §16, Phase 1.1 "basic expenses" cut).
// Currency is copied from the account at creation and never changes — an
// expense recorded under one denomination must not silently re-denominate if
// the farm's currency setting later changes. Amounts are minor units (cents).
// Direct flock allocation only in this slice; shared-allocation methods
// (bird-count / revenue share) come with profitability reporting.
public sealed class Expense : AggregateRoot<Guid>
{
    public const int MaxDescriptionLength = 200;
    public const int MaxNoteLength = 500;

    public Guid FarmId { get; private set; }
    public Guid ExpenseCategoryId { get; private set; }
    public DateOnly Date { get; private set; }
    public string Description { get; private set; } = string.Empty;
    public long AmountMinorUnits { get; private set; }
    public string CurrencyCode { get; private set; } = string.Empty;
    public int CurrencyMinorUnit { get; private set; }
    public Guid? FlockId { get; private set; }
    public string? Note { get; private set; }
    public int Version { get; private set; }

    private Expense() { }

    public static Expense Create(
        Guid id, Guid accountId, Guid farmId, Guid expenseCategoryId,
        DateOnly date, string description, long amountMinorUnits,
        string currencyCode, int currencyMinorUnit,
        Guid? flockId = null, string? note = null)
    {
        var guard = ValidateFields(description, amountMinorUnits, note);
        if (guard.IsFailure)
            throw new ArgumentException(guard.Error.Description);
        if (string.IsNullOrWhiteSpace(currencyCode))
            throw new ArgumentException("Currency code is required.", nameof(currencyCode));

        return new Expense
        {
            Id = id, AccountId = accountId, FarmId = farmId,
            ExpenseCategoryId = expenseCategoryId,
            Date = date,
            Description = description.Trim(),
            AmountMinorUnits = amountMinorUnits,
            CurrencyCode = currencyCode,
            CurrencyMinorUnit = currencyMinorUnit,
            FlockId = flockId,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()
        };
    }

    // Admin correction (F16 water pattern): edit in place under the Version
    // concurrency token. Category/flock retargeting is allowed — the handler
    // validates the new references; currency is deliberately not editable.
    public Result Adjust(
        Guid expenseCategoryId, DateOnly date, string description,
        long amountMinorUnits, Guid? flockId, string? note)
    {
        var guard = ValidateFields(description, amountMinorUnits, note);
        if (guard.IsFailure) return guard;

        ExpenseCategoryId = expenseCategoryId;
        Date = date;
        Description = description.Trim();
        AmountMinorUnits = amountMinorUnits;
        FlockId = flockId;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        Version++;
        return Result.Success();
    }

    private static Result ValidateFields(string description, long amountMinorUnits, string? note)
    {
        if (string.IsNullOrWhiteSpace(description))
            return Result.Failure(Error.Validation(
                "Expense.DescriptionRequired", "A description is required."));
        if (description.Trim().Length > MaxDescriptionLength)
            return Result.Failure(Error.Validation(
                "Expense.DescriptionTooLong",
                $"Description cannot exceed {MaxDescriptionLength} characters."));
        if (amountMinorUnits <= 0)
            return Result.Failure(Error.Validation(
                "Expense.AmountNotPositive", "Amount must be greater than zero."));
        if (note is not null && note.Trim().Length > MaxNoteLength)
            return Result.Failure(Error.Validation(
                "Expense.NoteTooLong", $"Note cannot exceed {MaxNoteLength} characters."));
        return Result.Success();
    }
}
