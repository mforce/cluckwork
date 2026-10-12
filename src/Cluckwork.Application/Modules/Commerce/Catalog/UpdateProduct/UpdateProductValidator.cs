using Cluckwork.Application.Modules.Commerce.Contracts;
using Cluckwork.Domain.Modules.Commerce.Catalog;
using FluentValidation;

namespace Cluckwork.Application.Modules.Commerce.Catalog.UpdateProduct;

public sealed class UpdateProductValidator : AbstractValidator<UpdateProductCommand>
{
    public UpdateProductValidator()
    {
        // An omitted Version binds to null, never 0 (UpdateCustomerValidator).
        RuleFor(x => x.Version).NotNull().WithErrorCode("Product.Version.Required")
            .GreaterThanOrEqualTo(0).WithErrorCode("Product.Version.NonNegative");
        RuleFor(x => x.Name).NotEmpty().WithErrorCode("Product.Name.Required")
            .MaximumLength(Product.MaxNameLength).WithErrorCode("Product.Name.MaxLength");
        // All products are egg products this phase, so the egg-unit whitelist
        // applies to updates too (codex review of #98).
        RuleFor(x => x.DefaultUnit)
            .Must(Cluckwork.Application.Modules.Commerce.Catalog.CreateProduct.CreateProductValidator.IsEggUnit)
            .WithMessage("Egg products sell per egg, dozen, flat, tray, carton, or case.")
            .WithErrorCode("Product.DefaultUnit.Allowed");
        RuleFor(x => x.DefaultPriceMinorUnits).GreaterThanOrEqualTo(0).WithErrorCode("Product.DefaultPrice.NonNegative")
            .When(x => x.DefaultPriceMinorUnits is not null);
        RuleFor(x => x.EggGradeId).NotEmpty()
            .WithMessage("An egg product must map to an egg grade.")
            .WithErrorCode("Product.EggGradeId.Required");
        RuleFor(x => x.Notes).MaximumLength(Product.MaxNotesLength).WithErrorCode("Product.Notes.MaxLength");
    }
}
