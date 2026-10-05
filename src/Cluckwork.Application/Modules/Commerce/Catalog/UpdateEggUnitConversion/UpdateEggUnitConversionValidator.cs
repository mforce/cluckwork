using Cluckwork.Application.Modules.Commerce.Contracts;
using FluentValidation;

namespace Cluckwork.Application.Modules.Commerce.Catalog.UpdateEggUnitConversion;

public sealed class UpdateEggUnitConversionValidator : AbstractValidator<UpdateEggUnitConversionCommand>
{
    public UpdateEggUnitConversionValidator()
    {
        RuleFor(x => x.EggsPerUnit).GreaterThanOrEqualTo(1).WithErrorCode("EggUnitConversion.EggsPerUnit.Min");
    }
}
