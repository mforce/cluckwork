namespace Cluckwork.Application.Features.Catalog.UpdateEggUnitConversion;

using FluentValidation;

public sealed class UpdateEggUnitConversionValidator : AbstractValidator<UpdateEggUnitConversionCommand>
{
    public UpdateEggUnitConversionValidator()
    {
        RuleFor(x => x.EggsPerUnit).GreaterThanOrEqualTo(1).WithErrorCode("EggUnitConversion.EggsPerUnit.Min");
    }
}
