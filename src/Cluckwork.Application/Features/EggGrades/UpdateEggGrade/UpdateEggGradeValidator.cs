namespace Cluckwork.Application.Features.EggGrades.UpdateEggGrade;

using Cluckwork.Domain.Eggs;
using FluentValidation;

public sealed class UpdateEggGradeValidator : AbstractValidator<UpdateEggGradeCommand>
{
    public UpdateEggGradeValidator()
    {
        RuleFor(c => c.EggGradeId).NotEmpty();

        RuleFor(c => c.Name)
            .Must(n => !string.IsNullOrWhiteSpace(n))
            .WithMessage("Grade name is required.")
            .Must(n => n is null || n.Trim().Length <= EggGrade.MaxNameLength)
            .WithMessage($"Grade name cannot exceed {EggGrade.MaxNameLength} characters.");
    }
}
