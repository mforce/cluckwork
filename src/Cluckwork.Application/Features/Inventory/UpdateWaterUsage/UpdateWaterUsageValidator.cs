namespace Cluckwork.Application.Features.Inventory.UpdateWaterUsage;

using Cluckwork.Domain.Inventory;
using FluentValidation;

public sealed class UpdateWaterUsageValidator : AbstractValidator<UpdateWaterUsageCommand>
{
    public UpdateWaterUsageValidator()
    {
        RuleFor(x => x.WaterUsageId).NotEmpty();
        RuleFor(x => x.Source)
            .Must(s => Enum.TryParse<WaterSource>(s, ignoreCase: true, out var parsed)
                       && Enum.IsDefined(parsed))
            .WithMessage("Source must be Well, Municipal, Tank, or Other.");
        RuleFor(x => x.Unit)
            .Must(u => u is null || WaterUsage.AllowedUnits.Contains(u))
            .WithMessage($"Unit must be one of: {string.Join(", ", WaterUsage.AllowedUnits)}.");
        RuleFor(x => x.Quantity)
            .GreaterThan(0)
            .LessThanOrEqualTo(1_000_000_000m)
            .Must(q => q is null || decimal.Round(q.Value, 3) == q.Value)
            .WithMessage("Quantity supports at most 3 decimal places.")
            .When(x => x.Quantity is not null);
        RuleFor(x => x)
            .Must(x => (x.MeterStart is null) == (x.MeterEnd is null))
            .WithName("MeterEnd")
            .WithMessage("Meter start and end must be provided together.");
        RuleFor(x => x)
            .Must(x => x.MeterStart is null
                       || (x.MeterStart >= 0 && x.MeterEnd > x.MeterStart
                           && x.MeterEnd - x.MeterStart <= 1_000_000_000m))
            .WithName("MeterEnd")
            .WithMessage("Meter end must be after meter start (non-negative, sane range).")
            .When(x => (x.MeterStart is null) == (x.MeterEnd is null));
        RuleFor(x => x)
            .Must(x => x.Quantity is not null || x.MeterStart is not null)
            .WithName("Quantity")
            .WithMessage("Provide a quantity or meter readings.");
        RuleFor(x => x)
            .Must(x => x.Quantity is null || x.MeterStart is null
                       || x.MeterEnd - x.MeterStart == x.Quantity)
            .WithName("Quantity")
            .WithMessage("Quantity must equal the meter delta (or be omitted to derive it).");
        RuleFor(x => x.Note)
            .MaximumLength(WaterUsage.MaxNoteLength);
    }
}
