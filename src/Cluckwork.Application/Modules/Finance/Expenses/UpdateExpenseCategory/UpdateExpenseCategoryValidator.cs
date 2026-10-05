using Cluckwork.Application.Modules.Finance.Contracts;
using Cluckwork.Domain.Modules.Finance.Expenses;
using FluentValidation;

namespace Cluckwork.Application.Modules.Finance.Expenses.UpdateExpenseCategory;

public sealed class UpdateExpenseCategoryValidator : AbstractValidator<UpdateExpenseCategoryCommand>
{
    public UpdateExpenseCategoryValidator()
    {
        RuleFor(c => c.Name)
            .Must(n => !string.IsNullOrWhiteSpace(n))
            .WithMessage("Category name is required.")
            .WithErrorCode("ExpenseCategory.Name.Required")
            .Must(n => n is null || n.Trim().Length <= ExpenseCategory.MaxNameLength)
            .WithMessage($"Category name cannot exceed {ExpenseCategory.MaxNameLength} characters.")
            .WithErrorCode("ExpenseCategory.Name.MaxLength");
    }
}
