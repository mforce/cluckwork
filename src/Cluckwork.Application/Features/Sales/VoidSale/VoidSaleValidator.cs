namespace Cluckwork.Application.Features.Sales.VoidSale;

using Cluckwork.Domain.Sales;
using FluentValidation;

public sealed class VoidSaleValidator : AbstractValidator<VoidSaleCommand>
{
    public VoidSaleValidator()
    {
        RuleFor(x => x.SalesOrderId).NotEmpty();
        // NotEmpty alone passes whitespace-only strings.
        RuleFor(x => x.Reason)
            .Must(r => !string.IsNullOrWhiteSpace(r))
            .WithMessage("A void reason is required.")
            .MaximumLength(SalesOrder.MaxVoidReasonLength);
    }
}
