namespace Cluckwork.Application.Features.Expenses.AdjustExpense;

using Cluckwork.Domain.Expenses;
using FluentValidation;

public sealed class AdjustExpenseValidator : AbstractValidator<AdjustExpenseCommand>
{
    public AdjustExpenseValidator()
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
            .Must(d => d <= DateOnly.FromDateTime(DateTime.UtcNow))
            .WithMessage("Expense date cannot be in the future.")
            // An omitted JSON date binds as 0001-01-01 — reject nonsense
            // instead of persisting year-1 rows (codex review of #88).
            .Must(d => d >= new DateOnly(2000, 1, 1))
            .WithMessage("Expense date is missing or unrealistically old.");
    }
}
