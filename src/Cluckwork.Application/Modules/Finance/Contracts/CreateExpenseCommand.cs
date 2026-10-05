namespace Cluckwork.Application.Modules.Finance.Contracts;

public sealed record CreateExpenseCommand(
    Guid ExpenseCategoryId,
    DateOnly Date,
    string Description,
    long AmountMinorUnits,
    Guid? FlockId,
    string? Note);
