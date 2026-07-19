namespace Cluckwork.Application.Features.Sales.VoidPayment;

using Cluckwork.Domain.Sales;
using FluentValidation;

public sealed class VoidPaymentValidator : AbstractValidator<VoidPaymentCommand>
{
    public VoidPaymentValidator()
    {
        RuleFor(c => c.Reason)
            .Must(r => !string.IsNullOrWhiteSpace(r))
            .WithMessage("A reason is required to void a payment.")
            .Must(r => r is null || r.Trim().Length <= Payment.MaxNoteLength)
            .WithMessage($"Reason cannot exceed {Payment.MaxNoteLength} characters.");

        RuleFor(c => c.Version)
            // A negative base version is a malformed request, not a conflict.
            .GreaterThanOrEqualTo(0)
            .WithMessage("Version must be zero or greater.");
    }
}
