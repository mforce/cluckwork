namespace Cluckwork.Application.Features.Flocks.RecordBirdMovement;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Flocks;
using FluentValidation;

public sealed class RecordBirdMovementValidator : AbstractValidator<RecordBirdMovementCommand>
{
    private static readonly string[] ManualTypes = [nameof(BirdMovementType.Cull), nameof(BirdMovementType.Adjustment)];

    public RecordBirdMovementValidator(IFarmClock farmClock)
    {
        RuleFor(c => c.FlockId).NotEmpty();

        RuleFor(c => c.Type)
            .Must(t => ManualTypes.Contains(t, StringComparer.OrdinalIgnoreCase))
            .WithMessage("Movement type must be Cull or Adjustment (mortality is generated from daily entries).");

        RuleFor(c => c.Quantity)
            .NotEqual(0)
            .WithMessage("Quantity cannot be zero.");

        RuleFor(c => c.Quantity)
            .GreaterThan(0)
            .When(c => nameof(BirdMovementType.Cull).Equals(c.Type, StringComparison.OrdinalIgnoreCase))
            .WithMessage("Culls remove birds — quantity must be positive.");

        RuleFor(c => c.Date)
            .NotEqual(default(DateOnly))
            .WithMessage("Date is required.")
            // #35: farm-local today, same boundary as daily entries — this rule
            // carried the identical UTC + 1-day slack.
            .MustAsync(async (date, ct) => date <= await farmClock.TodayAsync(ct))
            .WithMessage("Date cannot be in the future.");

        RuleFor(c => c.Note)
            .Must(n => n is null || n.Trim().Length <= BirdMovement.MaxNoteLength)
            .WithMessage($"Note cannot exceed {BirdMovement.MaxNoteLength} characters.");
    }
}
