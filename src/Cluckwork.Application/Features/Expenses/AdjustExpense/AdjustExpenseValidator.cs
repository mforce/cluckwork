namespace Cluckwork.Application.Features.Expenses.AdjustExpense;

using Cluckwork.Domain.Expenses;
using Cluckwork.Application.Common;
using FluentValidation;

public sealed class AdjustExpenseValidator : AbstractValidator<AdjustExpenseCommand>
{
    public AdjustExpenseValidator(IFarmClock farmClock)
    {
        RuleFor(c => c.Description)
            .Must(d => !string.IsNullOrWhiteSpace(d))
            .WithMessage("A description is required.")
            .WithErrorCode("Expense.Description.Required")
            .Must(d => d is null || d.Trim().Length <= Expense.MaxDescriptionLength)
            .WithMessage($"Description cannot exceed {Expense.MaxDescriptionLength} characters.")
            .WithErrorCode("Expense.Description.MaxLength");

        RuleFor(c => c.AmountMinorUnits)
            .GreaterThan(0)
            .WithMessage("Amount must be greater than zero.")
            .WithErrorCode("Expense.Amount.Positive");

        RuleFor(c => c.Note)
            .Must(n => n is null || n.Trim().Length <= Expense.MaxNoteLength)
            .WithMessage($"Note cannot exceed {Expense.MaxNoteLength} characters.")
            .WithErrorCode("Expense.Note.MaxLength");

        RuleFor(c => c.Date)
            // The farm's own today (#35).
            .MustAsync(async (d, ct) => d <= await farmClock.TodayAsync(ct))
            .WithMessage("Expense date cannot be in the future.")
            .WithErrorCode("Expense.Date.NotFuture")
            // An omitted JSON date binds as 0001-01-01 — reject nonsense
            // instead of persisting year-1 rows (codex review of #88).
            .Must(d => d >= new DateOnly(2000, 1, 1))
            .WithMessage("Expense date is missing or unrealistically old.")
            .WithErrorCode("Expense.Date.NotTooOld");
    }
}
