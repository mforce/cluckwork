namespace Cluckwork.Application.Features.Flocks.CreateFlock;

using Cluckwork.Application.Common;
using FluentValidation;

public sealed class CreateFlockValidator : AbstractValidator<CreateFlockCommand>
{
    public CreateFlockValidator(IFarmClock farmClock)
    {
        // Must-not-whitespace instead of bare NotEmpty: whitespace-only input
        // would pass NotEmpty and throw inside Flock.Create (a 500, not a 400).
        RuleFor(x => x.Name)
            .Must(n => !string.IsNullOrWhiteSpace(n))
            .WithMessage("Flock name is required.")
            .MaximumLength(Cluckwork.Domain.Flocks.Flock.MaxNameLength);
        RuleFor(x => x.Breed)
            .Must(b => !string.IsNullOrWhiteSpace(b))
            .WithMessage("Flock breed is required.")
            .MaximumLength(Cluckwork.Domain.Flocks.Flock.MaxBreedLength);
        RuleFor(x => x.InitialCount).GreaterThan(0);
        RuleFor(x => x.PlacementDate)
            .NotEqual(default(DateOnly))
            .WithMessage("Placement date is required.")
            // The farm's own today (#35).
            .MustAsync(async (d, ct) => d <= await farmClock.TodayAsync(ct))
            .WithMessage("Placement date cannot be in the future.");
    }
}
