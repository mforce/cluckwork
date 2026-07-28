namespace Cluckwork.Application.Features.EggGrades.UpdateEggGrade;

using Cluckwork.Domain.Eggs;
using FluentValidation;

public sealed class UpdateEggGradeValidator : AbstractValidator<UpdateEggGradeCommand>
{
    public UpdateEggGradeValidator()
    {
        RuleFor(c => c.EggGradeId).NotEmpty().WithErrorCode("EggGrade.EggGradeId.Required");

        RuleFor(c => c.Name)
            .Must(n => !string.IsNullOrWhiteSpace(n))
            .WithMessage("Grade name is required.")
            .WithErrorCode("EggGrade.Name.Required")
            .Must(n => n is null || n.Trim().Length <= EggGrade.MaxNameLength)
            .WithMessage($"Grade name cannot exceed {EggGrade.MaxNameLength} characters.")
            .WithErrorCode("EggGrade.Name.MaxLength");
    }
}
