namespace Cluckwork.Application.Features.Expenses.CreateExpense;

public sealed record CreateExpenseCommand(
    Guid ExpenseCategoryId,
    DateOnly Date,
    string Description,
    long AmountMinorUnits,
    Guid? FlockId,
    string? Note);
