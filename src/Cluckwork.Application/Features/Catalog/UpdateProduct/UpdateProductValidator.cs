namespace Cluckwork.Application.Features.Catalog.UpdateProduct;

using Cluckwork.Domain.Catalog;
using FluentValidation;

public sealed class UpdateProductValidator : AbstractValidator<UpdateProductCommand>
{
    public UpdateProductValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Product.MaxNameLength);
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
