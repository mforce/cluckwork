namespace Cluckwork.Application.Features.Sales.AddOrderItem;

using FluentValidation;

public sealed class AddOrderItemValidator : AbstractValidator<AddOrderItemCommand>
{
    public AddOrderItemValidator()
    {
        RuleFor(x => x.EggGradeId).NotEmpty();
        RuleFor(x => x.Quantity).GreaterThan(0);
        RuleFor(x => x.UnitPriceMinorUnits).GreaterThanOrEqualTo(0);
    }
}
