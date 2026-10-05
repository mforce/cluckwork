namespace Cluckwork.Application.Modules.Finance.Contracts;

public sealed record AdjustExpenseCommand(
    Guid ExpenseId,
    int Version,
    Guid ExpenseCategoryId,
    DateOnly Date,
    string Description,
    long AmountMinorUnits,
    Guid? FlockId,
    string? Note);
