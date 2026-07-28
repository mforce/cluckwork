namespace Cluckwork.Application.Features.Catalog.CreateProduct;

using Cluckwork.Domain.Catalog;
using FluentValidation;

public sealed class CreateProductValidator : AbstractValidator<CreateProductCommand>
{
    // Egg products may only use units that resolve to eggs through
    // EggUnitConversion — Bird/Lb/Kg/Package would persist fine but leave the
    // product unsellable, and Other has no conversion row this phase (codex
    // review of #98).
    internal static readonly ProductUnit[] EggUnits =
        [ProductUnit.Egg, ProductUnit.Dozen, ProductUnit.Flat,
         ProductUnit.Tray, ProductUnit.Carton, ProductUnit.Case];

    // Enum.TryParse accepts bare numerals ("999" → undefined value) — only a
    // spelled-out defined name counts.
    internal static bool IsEggUnit(string input) =>
        !string.IsNullOrEmpty(input) && char.IsLetter(input[0])
        && Enum.TryParse<ProductUnit>(input, ignoreCase: true, out var parsed)
        && EggUnits.Contains(parsed);

    public CreateProductValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithErrorCode("Product.Name.Required")
            .MaximumLength(Product.MaxNameLength).WithErrorCode("Product.Name.MaxLength");
        RuleFor(x => x.ProductType)
            // Only egg products are sellable today; the other types exist in
            // the enum so the schema is ready, but creating one would produce
            // a product nothing can price, allocate, or sell (#97 part 1).
            .Must(t => !string.IsNullOrEmpty(t) && char.IsLetter(t[0])
                       && Enum.TryParse<ProductType>(t, ignoreCase: true, out var parsed)
                       && parsed == ProductType.Egg)
            .WithMessage("Only egg products can be created in this phase.")
            .WithErrorCode("Product.ProductType.Allowed");
        RuleFor(x => x.DefaultUnit)
            .Must(IsEggUnit)
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
