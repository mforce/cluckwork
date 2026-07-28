namespace Cluckwork.Application.Features.Expenses.CreateExpenseCategory;

using Cluckwork.Domain.Expenses;
using FluentValidation;

public sealed class CreateExpenseCategoryValidator : AbstractValidator<CreateExpenseCategoryCommand>
{
    public CreateExpenseCategoryValidator()
    {
        RuleFor(c => c.Name)
            // NotEmpty alone lets whitespace-only through, which would throw in
            // ExpenseCategory.Create and surface as a 500 instead of a 400.
            .Must(n => !string.IsNullOrWhiteSpace(n))
            .WithMessage("Category name is required.")
            .WithErrorCode("ExpenseCategory.Name.Required")
            .Must(n => n is null || n.Trim().Length <= ExpenseCategory.MaxNameLength)
            .WithMessage($"Category name cannot exceed {ExpenseCategory.MaxNameLength} characters.")
            .WithErrorCode("ExpenseCategory.Name.MaxLength");
    }
}
