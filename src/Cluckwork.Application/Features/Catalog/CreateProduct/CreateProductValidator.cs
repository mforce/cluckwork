namespace Cluckwork.Application.Features.Catalog.CreateProduct;

using Cluckwork.Domain.Catalog;
using FluentValidation;

public sealed class CreateProductValidator : AbstractValidator<CreateProductCommand>
{
    public CreateProductValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Product.MaxNameLength);
        RuleFor(x => x.ProductType)
            .Must(t => Enum.TryParse<ProductType>(t, ignoreCase: true, out _))
            .WithMessage("Unknown product type.")
            // Only egg products are sellable today; the other types exist in
            // the enum so the schema is ready, but creating one would produce
            // a product nothing can price, allocate, or sell (#97 part 1).
            .Must(t => Enum.TryParse<ProductType>(t, ignoreCase: true, out var parsed)
                       && parsed == ProductType.Egg)
            .WithMessage("Only egg products can be created in this phase.");
        RuleFor(x => x.DefaultUnit)
            .Must(u => Enum.TryParse<ProductUnit>(u, ignoreCase: true, out _))
            .WithMessage("Unknown unit.");
        RuleFor(x => x.DefaultPriceMinorUnits).GreaterThanOrEqualTo(0)
            .When(x => x.DefaultPriceMinorUnits is not null);
        RuleFor(x => x.EggGradeId).NotEmpty()
            .WithMessage("An egg product must map to an egg grade.");
        RuleFor(x => x.Notes).MaximumLength(Product.MaxNotesLength);
    }
}
