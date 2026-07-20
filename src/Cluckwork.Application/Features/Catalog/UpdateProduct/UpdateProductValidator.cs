namespace Cluckwork.Application.Features.Catalog.UpdateProduct;

using Cluckwork.Domain.Catalog;
using FluentValidation;

public sealed class UpdateProductValidator : AbstractValidator<UpdateProductCommand>
{
    public UpdateProductValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Product.MaxNameLength);
        // All products are egg products this phase, so the egg-unit whitelist
        // applies to updates too (codex review of #98).
        RuleFor(x => x.DefaultUnit)
            .Must(CreateProduct.CreateProductValidator.IsEggUnit)
            .WithMessage("Egg products sell per egg, dozen, flat, tray, carton, or case.");
        RuleFor(x => x.DefaultPriceMinorUnits).GreaterThanOrEqualTo(0)
            .When(x => x.DefaultPriceMinorUnits is not null);
        RuleFor(x => x.EggGradeId).NotEmpty()
            .WithMessage("An egg product must map to an egg grade.");
        RuleFor(x => x.Notes).MaximumLength(Product.MaxNotesLength);
    }
}
