namespace Cluckwork.Application.Features.Inventory.RecordWaterUsage;

using Cluckwork.Domain.Inventory;
using FluentValidation;

public sealed class RecordWaterUsageValidator : AbstractValidator<RecordWaterUsageCommand>
{
    public RecordWaterUsageValidator()
    {
        RuleFor(x => x.FlockId).NotEmpty().WithErrorCode("WaterUsage.FlockId.Required");
        RuleFor(x => x.Date).NotEmpty()
            .WithMessage("Usage date is required.").WithErrorCode("WaterUsage.Date.Required");
        RuleFor(x => x.Source)
            .NotEmpty().WithErrorCode("WaterUsage.Source.Required")
            .Must(s => Enum.GetNames<WaterSource>()
                .Contains(s, StringComparer.OrdinalIgnoreCase))
            .WithMessage("Source must be Well, Municipal, Tank, or Other.").WithErrorCode("WaterUsage.Source.Allowed");
        RuleFor(x => x.Unit)
            .Must(u => u is null || WaterUsage.AllowedUnits.Contains(u))
            .WithMessage($"Unit must be one of: {string.Join(", ", WaterUsage.AllowedUnits)}.").WithErrorCode("WaterUsage.Unit.Allowed");
        RuleFor(x => x.Quantity)
            .GreaterThan(0).WithErrorCode("WaterUsage.Quantity.Positive")
            .LessThanOrEqualTo(1_000_000_000m).WithErrorCode("WaterUsage.Quantity.Max")
            .Must(q => q is null || decimal.Round(q.Value, 3) == q.Value)
            .WithMessage("Quantity supports at most 3 decimal places.").WithErrorCode("WaterUsage.Quantity.Precision")
            .When(x => x.Quantity is not null);
        // Meters travel as a pair, non-negative, end after start.
        RuleFor(x => x)
            .Must(x => (x.MeterStart is null) == (x.MeterEnd is null))
            .WithName("MeterEnd")
            .WithMessage("Meter start and end must be provided together.").WithErrorCode("WaterUsage.MeterEnd.Paired");
        RuleFor(x => x)
            .Must(x => (x.MeterStart is null || (decimal.Round(x.MeterStart.Value, 3) == x.MeterStart
                            && x.MeterStart <= 1_000_000_000_000m))
                       && (x.MeterEnd is null || (decimal.Round(x.MeterEnd.Value, 3) == x.MeterEnd
                            && x.MeterEnd <= 1_000_000_000_000m)))
            .WithName("MeterEnd")
            .WithMessage("Meter readings support at most 3 decimal places (sane range).").WithErrorCode("WaterUsage.MeterEnd.Precision");
        RuleFor(x => x)
            .Must(x => x.MeterStart is null
                       || (x.MeterStart >= 0 && x.MeterEnd > x.MeterStart
                           && x.MeterEnd - x.MeterStart <= 1_000_000_000m))
            .WithName("MeterEnd")
            .WithMessage("Meter end must exceed meter start (non-negative, sane range).").WithErrorCode("WaterUsage.MeterEnd.Ordering")
            .When(x => x.MeterStart is not null && x.MeterEnd is not null);
        // Without meters a quantity is mandatory; with both, they must agree.
        RuleFor(x => x)
            .Must(x => x.Quantity is not null || x.MeterStart is not null)
            .WithName("Quantity")
            .WithMessage("Provide a quantity or meter readings.").WithErrorCode("WaterUsage.Quantity.RequiredOrMeter");
        RuleFor(x => x)
            .Must(x => x.Quantity is null || x.MeterStart is null
                       || x.MeterEnd - x.MeterStart == x.Quantity)
            .WithName("Quantity")
            .WithMessage("Quantity must equal the meter delta (or be omitted to derive it).").WithErrorCode("WaterUsage.Quantity.MatchesMeterDelta");
        RuleFor(x => x.Note)
            .MaximumLength(WaterUsage.MaxNoteLength).WithErrorCode("WaterUsage.Note.MaxLength");
    }
}
