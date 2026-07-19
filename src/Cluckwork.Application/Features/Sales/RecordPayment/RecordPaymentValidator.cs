namespace Cluckwork.Application.Features.Sales.RecordPayment;

using Cluckwork.Domain.Sales;
using FluentValidation;

public sealed class RecordPaymentValidator : AbstractValidator<RecordPaymentCommand>
{
    public RecordPaymentValidator()
    {
        RuleFor(c => c.AmountMinorUnits)
            .GreaterThan(0)
            .WithMessage("Payment amount must be greater than zero.");

        RuleFor(c => c.Method)
            // TryParse alone accepts numeric strings like "999"; IsDefined pins
            // the value to the declared enum members (grade-type pattern).
            .Must(m => Enum.TryParse<PaymentMethod>(m, ignoreCase: true, out var parsed)
                       && Enum.IsDefined(parsed))
            .WithMessage("Method must be one of: Cash, Check, Card, BankTransfer, MobilePayment, Other.");

        RuleFor(c => c.ReferenceNumber)
            .Must(r => r is null || r.Trim().Length <= Payment.MaxReferenceLength)
            .WithMessage($"Reference cannot exceed {Payment.MaxReferenceLength} characters.");

        RuleFor(c => c.Note)
            .Must(n => n is null || n.Trim().Length <= Payment.MaxNoteLength)
            .WithMessage($"Note cannot exceed {Payment.MaxNoteLength} characters.");

        RuleFor(c => c.PaymentDate)
            // UTC "today" until farm-local timezones land (#35).
            .Must(d => d <= DateOnly.FromDateTime(DateTime.UtcNow))
            .WithMessage("Payment date cannot be in the future.")
            // An omitted JSON date binds as 0001-01-01 (codex review of #88).
            .Must(d => d >= new DateOnly(2000, 1, 1))
            .WithMessage("Payment date is missing or unrealistically old.");
    }
}
