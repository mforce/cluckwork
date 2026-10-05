namespace Cluckwork.Application.Features.Expenses.AdjustExpense;

[ModuleContract("Finance")]
public sealed record AdjustExpenseCommand(
    Guid ExpenseId,
    int Version,
    Guid ExpenseCategoryId,
    DateOnly Date,
    string Description,
    long AmountMinorUnits,
    Guid? FlockId,
    string? Note);
