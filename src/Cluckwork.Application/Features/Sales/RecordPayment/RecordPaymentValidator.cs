namespace Cluckwork.Application.Features.Sales.RecordPayment;

using Cluckwork.Domain.Sales;
using Cluckwork.Application.Common;
using FluentValidation;

public sealed class RecordPaymentValidator : AbstractValidator<RecordPaymentCommand>
{
    public RecordPaymentValidator(IFarmClock farmClock)
    {
        RuleFor(c => c.AmountMinorUnits)
            .GreaterThan(0)
            .WithMessage("Payment amount must be greater than zero.")
            .WithErrorCode("Payment.Amount.Positive");

        RuleFor(c => c.Method)
            // TryParse alone accepts numeric strings — both out-of-range ("999")
            // and in-range ("3") — so the digits check pins the contract to the
            // NAMES only (codex review of #90).
            .Must(m => m is not null
                       && !m.Trim().All(char.IsDigit)
                       && Enum.TryParse<PaymentMethod>(m, ignoreCase: true, out var parsed)
                       && Enum.IsDefined(parsed))
            .WithMessage("Method must be one of: Cash, Check, Card, BankTransfer, MobilePayment, Other.")
            .WithErrorCode("Payment.Method.Allowed");

        RuleFor(c => c.ReferenceNumber)
            .Must(r => r is null || r.Trim().Length <= Payment.MaxReferenceLength)
            .WithMessage($"Reference cannot exceed {Payment.MaxReferenceLength} characters.")
            .WithErrorCode("Payment.ReferenceNumber.MaxLength");

        RuleFor(c => c.Note)
            .Must(n => n is null || n.Trim().Length <= Payment.MaxNoteLength)
            .WithMessage($"Note cannot exceed {Payment.MaxNoteLength} characters.")
            .WithErrorCode("Payment.Note.MaxLength");

        RuleFor(c => c.PaymentDate)
            // The farm's own today (#35), the same boundary every other
            // date-gated rule reads.
            .MustAsync(async (d, ct) => d <= await farmClock.TodayAsync(ct))
            .WithMessage("Payment date cannot be in the future.")
            .WithErrorCode("Payment.PaymentDate.NotFuture")
            // An omitted JSON date binds as 0001-01-01 (codex review of #88).
            .Must(d => d >= new DateOnly(2000, 1, 1))
            .WithMessage("Payment date is missing or unrealistically old.")
            .WithErrorCode("Payment.PaymentDate.NotTooOld");
    }
}
