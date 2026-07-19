namespace Cluckwork.Domain.Expenses;

// Expense category lookup (spec §16) — per-farm buckets ("Feed", "Vet",
// "Repairs"). Expenses reference these rows; deactivating a category hides it
// from new expenses while recorded ones keep rendering it (the grandfathering
// pattern grades established).
public sealed class ExpenseCategory : AggregateRoot<Guid>
{
    public const int MaxNameLength = 100;

    public Guid FarmId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public bool Active { get; private set; }
    public int Version { get; private set; }

    private ExpenseCategory() { }

    public static ExpenseCategory Create(Guid id, Guid accountId, Guid farmId, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Category name is required.", nameof(name));
        if (name.Trim().Length > MaxNameLength)
            throw new ArgumentException($"Category name cannot exceed {MaxNameLength} characters.", nameof(name));

        return new ExpenseCategory
        {
            Id = id, AccountId = accountId, FarmId = farmId,
            Name = name.Trim(),
            Active = true
        };
    }

    public Result Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure(Error.Validation("ExpenseCategory.NameRequired", "Category name is required."));
        if (name.Trim().Length > MaxNameLength)
            return Result.Failure(Error.Validation(
                "ExpenseCategory.NameTooLong", $"Category name cannot exceed {MaxNameLength} characters."));

        Name = name.Trim();
        Version++;
        return Result.Success();
    }

    public Result Deactivate()
    {
        if (!Active)
            return Result.Failure(Error.Domain("ExpenseCategory.NotActive", "Category is already inactive."));
        Active = false;
        Version++;
        return Result.Success();
    }

    public Result Activate()
    {
        if (Active)
            return Result.Failure(Error.Domain("ExpenseCategory.AlreadyActive", "Category is already active."));
        Active = true;
        Version++;
        return Result.Success();
    }
}
