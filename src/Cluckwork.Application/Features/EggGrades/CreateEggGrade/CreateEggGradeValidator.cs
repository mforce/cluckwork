namespace Cluckwork.Application.Features.EggGrades.CreateEggGrade;

using Cluckwork.Domain.Eggs;
using FluentValidation;

public sealed class CreateEggGradeValidator : AbstractValidator<CreateEggGradeCommand>
{
    public CreateEggGradeValidator()
    {
        RuleFor(c => c.Name)
            // NotEmpty alone lets whitespace-only through, which would throw in
            // EggGrade.Create and surface as a 500 instead of a 400.
            .Must(n => !string.IsNullOrWhiteSpace(n))
            .WithMessage("Grade name is required.")
            .Must(n => n is null || n.Trim().Length <= EggGrade.MaxNameLength)
            .WithMessage($"Grade name cannot exceed {EggGrade.MaxNameLength} characters.");

        RuleFor(c => c.GradeType)
            // TryParse alone accepts numeric strings like "999"; IsDefined pins
            // the value to the declared enum members.
            .Must(t => Enum.TryParse<EggGradeType>(t, ignoreCase: true, out var parsed)
                       && Enum.IsDefined(parsed))
            .WithMessage("Grade type must be one of: Size, Quality, Custom.");
    }
}
