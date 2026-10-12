using Cluckwork.Application.Modules.Commerce.Contracts;
using FluentValidation;

namespace Cluckwork.Application.Modules.Commerce.Catalog.UpdateEggUnitConversion;

public sealed class UpdateEggUnitConversionValidator : AbstractValidator<UpdateEggUnitConversionCommand>
{
    public UpdateEggUnitConversionValidator()
    {
        // An omitted Version binds to null, never 0 (UpdateCustomerValidator).
        RuleFor(x => x.Version).NotNull().WithErrorCode("EggUnitConversion.Version.Required")
            .GreaterThanOrEqualTo(0).WithErrorCode("EggUnitConversion.Version.NonNegative");
        RuleFor(x => x.EggsPerUnit).GreaterThanOrEqualTo(1).WithErrorCode("EggUnitConversion.EggsPerUnit.Min");
    }
}
