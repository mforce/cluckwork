namespace Cluckwork.Application.Features.Flocks.CreateFlock;

using FluentValidation;

public sealed class CreateFlockValidator : AbstractValidator<CreateFlockCommand>
{
    public CreateFlockValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Breed).NotEmpty().MaximumLength(100);
        RuleFor(x => x.InitialCount).GreaterThan(0);
        RuleFor(x => x.PlacementDate)
            .NotEqual(default(DateOnly))
            .WithMessage("Placement date is required.");
    }
}
