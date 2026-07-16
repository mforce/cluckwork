namespace Cluckwork.Application.Features.Sales.UpdateOrderItem;

using FluentValidation;

public sealed class UpdateOrderItemValidator : AbstractValidator<UpdateOrderItemCommand>
{
    public UpdateOrderItemValidator()
    {
        RuleFor(x => x.ItemId).NotEmpty();
        RuleFor(x => x.Quantity).GreaterThan(0);
        RuleFor(x => x.UnitPriceMinorUnits).GreaterThanOrEqualTo(0);
        // Same overflow bound as AddOrderItem — Money.Multiply is unchecked.
        RuleFor(x => x)
            .Must(x => x.Quantity <= 0 || x.UnitPriceMinorUnits <= long.MaxValue / x.Quantity)
            .WithName("UnitPriceMinorUnits")
            .WithMessage("Line total exceeds the supported amount range.");
    }
}
