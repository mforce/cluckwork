namespace Cluckwork.Application.Features.Expenses.CreateExpense;

using Cluckwork.Domain.Expenses;
using FluentValidation;

public sealed class CreateExpenseValidator : AbstractValidator<CreateExpenseCommand>
{
    public CreateExpenseValidator()
    {
        RuleFor(c => c.Description)
            .Must(d => !string.IsNullOrWhiteSpace(d))
            .WithMessage("A description is required.")
            .Must(d => d is null || d.Trim().Length <= Expense.MaxDescriptionLength)
            .WithMessage($"Description cannot exceed {Expense.MaxDescriptionLength} characters.");

        RuleFor(c => c.AmountMinorUnits)
            .GreaterThan(0)
            .WithMessage("Amount must be greater than zero.");

        RuleFor(c => c.Note)
            .Must(n => n is null || n.Trim().Length <= Expense.MaxNoteLength)
            .WithMessage($"Note cannot exceed {Expense.MaxNoteLength} characters.");

        RuleFor(c => c.Date)
            // UTC "today" until farm-local timezones land (#35) — the same
            // convention every date-gated screen uses.
            .Must(d => d <= DateOnly.FromDateTime(DateTime.UtcNow))
            .WithMessage("Expense date cannot be in the future.");
    }
}
