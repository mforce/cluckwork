namespace Cluckwork.Application.Features.Expenses.CreateExpense;

[ModuleContract("Finance")]
public sealed record CreateExpenseCommand(
    Guid ExpenseCategoryId,
    DateOnly Date,
    string Description,
    long AmountMinorUnits,
    Guid? FlockId,
    string? Note);
