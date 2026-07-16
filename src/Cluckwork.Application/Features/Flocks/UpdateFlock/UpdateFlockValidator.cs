namespace Cluckwork.Application.Features.Flocks.UpdateFlock;

using Cluckwork.Domain.Flocks;
using FluentValidation;

public sealed class UpdateFlockValidator : AbstractValidator<UpdateFlockCommand>
{
    public UpdateFlockValidator()
    {
        RuleFor(x => x.FlockId).NotEmpty();

        // NotEmpty alone lets whitespace-only through (grade-management review
        // finding); the Must guard keeps that a 400, not a deeper failure.
        RuleFor(x => x.Name)
            .Must(n => !string.IsNullOrWhiteSpace(n))
            .WithMessage("Flock name is required.")
            .MaximumLength(Flock.MaxNameLength);
        RuleFor(x => x.Breed)
            .Must(b => !string.IsNullOrWhiteSpace(b))
            .WithMessage("Flock breed is required.")
            .MaximumLength(Flock.MaxBreedLength);
        RuleFor(x => x.InitialCount).GreaterThan(0);
        RuleFor(x => x.PlacementDate)
            .NotEqual(default(DateOnly))
            .WithMessage("Placement date is required.")
            .LessThanOrEqualTo(_ => DateOnly.FromDateTime(DateTime.UtcNow.Date))
            .WithMessage("Placement date cannot be in the future.");
    }
}
