namespace Cluckwork.Application.Features.Expenses.CreateExpenseCategory;

[ModuleContract("Finance")]
public sealed record CreateExpenseCategoryCommand(string Name);
