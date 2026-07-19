namespace Cluckwork.Application.Features.Expenses.UpdateExpenseCategory;

using Cluckwork.Domain.Expenses;
using FluentValidation;

public sealed class UpdateExpenseCategoryValidator : AbstractValidator<UpdateExpenseCategoryCommand>
{
    public UpdateExpenseCategoryValidator()
    {
        RuleFor(c => c.Name)
            .Must(n => !string.IsNullOrWhiteSpace(n))
            .WithMessage("Category name is required.")
            .Must(n => n is null || n.Trim().Length <= ExpenseCategory.MaxNameLength)
            .WithMessage($"Category name cannot exceed {ExpenseCategory.MaxNameLength} characters.");
    }
}
