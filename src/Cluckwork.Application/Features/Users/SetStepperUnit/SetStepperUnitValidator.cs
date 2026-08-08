namespace Cluckwork.Application.Features.Users.SetStepperUnit;

using Cluckwork.Domain.Catalog;
using FluentValidation;

public sealed class SetStepperUnitValidator : AbstractValidator<SetStepperUnitCommand>
{
    public SetStepperUnitValidator()
    {
        // null clears; otherwise a defined EggUnit name. Same round-trip check
        // as UpdateFarmSettingsValidator.BeEnumName — Enum.TryParse accepts
        // more than the wire contract offers (numbers, comma-OR'd flags-style
        // lists), so the parse is round-tripped back to its name.
        RuleFor(x => x.Unit)
            .Must(BeNullOrEggUnitName)
            .WithMessage("Stepper unit must be one of the farm's egg units, for example Individual or Tray.")
            .WithErrorCode("Me.StepperUnit.Format");
    }

    private static bool BeNullOrEggUnitName(string? value) =>
        value is null
        || (Enum.TryParse<EggUnit>(value, ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed)
            && string.Equals(parsed.ToString(), value.Trim(), StringComparison.OrdinalIgnoreCase));
}
