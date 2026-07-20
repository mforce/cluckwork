namespace Cluckwork.Application.Features.Sales.AddOrderItem;

using FluentValidation;

public sealed class AddOrderItemValidator : AbstractValidator<AddOrderItemCommand>
{
    public AddOrderItemValidator()
    {
        RuleFor(x => x.ProductId).NotEmpty();
        RuleFor(x => x.Quantity).GreaterThan(0);
        // Same whitelist as the catalog: only units that resolve to eggs.
        RuleFor(x => x.Unit)
            .Must(u => u is null || Catalog.CreateProduct.CreateProductValidator.IsEggUnit(u))
            .WithMessage("Egg products sell per egg, dozen, flat, tray, carton, or case.");
        RuleFor(x => x.UnitPriceMinorUnits).GreaterThanOrEqualTo(0)
            .When(x => x.UnitPriceMinorUnits is not null);
        // quantity * price must not overflow long (Money.Multiply is unchecked) —
        // wrap-around would store a negative line/order total.
        RuleFor(x => x)
            .Must(x => x.UnitPriceMinorUnits is not { } p || x.Quantity <= 0
                       || p <= long.MaxValue / x.Quantity)
            .WithName("UnitPriceMinorUnits")
            .WithMessage("Line total exceeds the supported amount range.");
    }
}
