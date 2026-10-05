using Cluckwork.Application.Modules.EggOperations.Contracts;
using Cluckwork.Domain.Modules.EggOperations.Eggs;
using FluentValidation;

namespace Cluckwork.Application.Modules.EggOperations.EggGrades.UpdateEggGrade;

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

        // #911 — absent means no warning; negative is nonsense (see the create
        // validator).
        RuleFor(c => c.LowStockFloor)
            .GreaterThanOrEqualTo(0)
            .When(c => c.LowStockFloor.HasValue)
            .WithMessage("Low-stock floor cannot be negative.")
            .WithErrorCode("EggGrade.LowStockFloor.Range");
    }
}
