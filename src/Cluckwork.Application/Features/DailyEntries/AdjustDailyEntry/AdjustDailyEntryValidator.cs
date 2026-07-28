namespace Cluckwork.Application.Features.DailyEntries.AdjustDailyEntry;

using Cluckwork.Domain.Eggs;
using FluentValidation;

public sealed class AdjustDailyEntryValidator : AbstractValidator<AdjustDailyEntryCommand>
{
    public AdjustDailyEntryValidator()
    {
        RuleFor(x => x.DailyEntryId).NotEmpty().WithErrorCode("DailyEntry.DailyEntryId.Required");
        RuleFor(x => x.Version).GreaterThanOrEqualTo(0).WithErrorCode("DailyEntry.Version.NonNegative");
        RuleFor(x => x.TotalEggs).GreaterThanOrEqualTo(0).WithErrorCode("DailyEntry.TotalEggs.NonNegative");
        RuleFor(x => x.CrackedEggs).GreaterThanOrEqualTo(0).WithErrorCode("DailyEntry.CrackedEggs.NonNegative");
        RuleFor(x => x.DirtyEggs).GreaterThanOrEqualTo(0).WithErrorCode("DailyEntry.DirtyEggs.NonNegative");
        RuleFor(x => x.DiscardedEggs).GreaterThanOrEqualTo(0).WithErrorCode("DailyEntry.DiscardedEggs.NonNegative");
        RuleFor(x => x.MortalityCount).GreaterThanOrEqualTo(0).WithErrorCode("DailyEntry.MortalityCount.NonNegative");
        // long accumulation — three ints can overflow past a positive total.
        RuleFor(x => x)
            .Must(x => (long)x.CrackedEggs + x.DirtyEggs + x.DiscardedEggs <= x.TotalEggs)
            .WithName("Eggs")
            .WithMessage("Cracked + dirty + discarded cannot exceed total eggs.")
            .WithErrorCode("DailyEntry.Eggs.SumWithinTotal");
        // NotEmpty alone lets "   " through to the domain guard (500, not 400).
        RuleFor(x => x.Reason)
            .Must(v => !string.IsNullOrWhiteSpace(v)).WithMessage("A reason is required.")
            .WithErrorCode("DailyEntry.Reason.Required")
            .MaximumLength(DailyEntry.MaxReasonLength)
            .WithErrorCode("DailyEntry.Reason.MaxLength");

        When(x => x.Grades is { Count: > 0 }, () =>
        {
            RuleForEach(x => x.Grades!).ChildRules(g =>
            {
                g.RuleFor(x => x.EggGradeId).NotEmpty().WithErrorCode("DailyEntry.GradeEggGradeId.Required");
                g.RuleFor(x => x.Quantity).GreaterThan(0).WithErrorCode("DailyEntry.GradeQuantity.Positive");
            });

            RuleFor(x => x.Grades!)
                .Must(grades => grades.Select(g => g.EggGradeId).Distinct().Count() == grades.Count)
                .WithMessage("Each grade may appear only once.")
                .WithErrorCode("DailyEntry.Grades.Unique");
        });
    }
}
