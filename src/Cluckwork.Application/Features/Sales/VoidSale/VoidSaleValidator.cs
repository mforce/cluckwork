namespace Cluckwork.Application.Features.Sales.VoidSale;

using Cluckwork.Domain.Sales;
using FluentValidation;

public sealed class VoidSaleValidator : AbstractValidator<VoidSaleCommand>
{
    public VoidSaleValidator()
    {
        RuleFor(x => x.SalesOrderId).NotEmpty();
        // NotEmpty alone passes whitespace-only strings. Length is checked on
        // the trimmed value — the domain stores reason.Trim(), and rejecting a
        // fitting reason for its surrounding whitespace would be a surprise.
        RuleFor(x => x.Reason)
            .Must(r => !string.IsNullOrWhiteSpace(r))
            .WithMessage("A void reason is required.")
            .Must(r => r is null || r.Trim().Length <= SalesOrder.MaxVoidReasonLength)
            .WithMessage($"Void reason must be at most {SalesOrder.MaxVoidReasonLength} characters.");
    }
}
