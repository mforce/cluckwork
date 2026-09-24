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

        // #911 — a floor is a count of eggs. Absent (null) is the ordinary
        // state and means no warning; present and negative is nonsense the
        // aggregate would throw on.
        RuleFor(c => c.LowStockFloor)
            .GreaterThanOrEqualTo(0)
            .When(c => c.LowStockFloor.HasValue)
            .WithMessage("Low-stock floor cannot be negative.")
            .WithErrorCode("EggGrade.LowStockFloor.Range");
    }
}
