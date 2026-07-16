namespace Cluckwork.Application.Features.EggGrades.CreateEggGrade;

using Cluckwork.Domain.Eggs;
using FluentValidation;

public sealed class CreateEggGradeValidator : AbstractValidator<CreateEggGradeCommand>
{
    public CreateEggGradeValidator()
    {
        RuleFor(c => c.Name)
            .NotEmpty()
            .Must(n => n is null || n.Trim().Length <= EggGrade.MaxNameLength)
            .WithMessage($"Grade name cannot exceed {EggGrade.MaxNameLength} characters.");

        RuleFor(c => c.GradeType)
            .Must(t => Enum.TryParse<EggGradeType>(t, ignoreCase: true, out _))
            .WithMessage("Grade type must be one of: Size, Quality, Custom.");
    }
}
