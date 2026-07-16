namespace Cluckwork.Application.Features.Sales.AddOrderItem;

using FluentValidation;

public sealed class AddOrderItemValidator : AbstractValidator<AddOrderItemCommand>
{
    public AddOrderItemValidator()
    {
        RuleFor(x => x.EggGradeId).NotEmpty();
        RuleFor(x => x.Quantity).GreaterThan(0);
        RuleFor(x => x.UnitPriceMinorUnits).GreaterThanOrEqualTo(0);
        // quantity * price must not overflow long (Money.Multiply is unchecked) —
        // wrap-around would store a negative line/order total.
        RuleFor(x => x)
            .Must(x => x.Quantity <= 0 || x.UnitPriceMinorUnits <= long.MaxValue / x.Quantity)
            .WithName("UnitPriceMinorUnits")
            .WithMessage("Line total exceeds the supported amount range.");
    }
}
