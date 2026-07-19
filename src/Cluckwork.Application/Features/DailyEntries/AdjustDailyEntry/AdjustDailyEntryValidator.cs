namespace Cluckwork.Application.Features.DailyEntries.AdjustDailyEntry;

using Cluckwork.Domain.Eggs;
using FluentValidation;

public sealed class AdjustDailyEntryValidator : AbstractValidator<AdjustDailyEntryCommand>
{
    public AdjustDailyEntryValidator()
    {
        RuleFor(x => x.DailyEntryId).NotEmpty();
        RuleFor(x => x.Version).GreaterThanOrEqualTo(0);
        RuleFor(x => x.TotalEggs).GreaterThanOrEqualTo(0);
        RuleFor(x => x.CrackedEggs).GreaterThanOrEqualTo(0);
        RuleFor(x => x.DirtyEggs).GreaterThanOrEqualTo(0);
        RuleFor(x => x.DiscardedEggs).GreaterThanOrEqualTo(0);
        RuleFor(x => x.MortalityCount).GreaterThanOrEqualTo(0);
        // long accumulation — three ints can overflow past a positive total.
        RuleFor(x => x)
            .Must(x => (long)x.CrackedEggs + x.DirtyEggs + x.DiscardedEggs <= x.TotalEggs)
            .WithName("Eggs")
            .WithMessage("Cracked + dirty + discarded cannot exceed total eggs.");
        // NotEmpty alone lets "   " through to the domain guard (500, not 400).
        RuleFor(x => x.Reason)
            .Must(v => !string.IsNullOrWhiteSpace(v)).WithMessage("A reason is required.")
            .MaximumLength(DailyEntry.MaxReasonLength);

        When(x => x.Grades is { Count: > 0 }, () =>
        {
            RuleForEach(x => x.Grades!).ChildRules(g =>
            {
                g.RuleFor(x => x.EggGradeId).NotEmpty();
                g.RuleFor(x => x.Quantity).GreaterThan(0);
            });

            RuleFor(x => x.Grades!)
                .Must(grades => grades.Select(g => g.EggGradeId).Distinct().Count() == grades.Count)
                .WithMessage("Each grade may appear only once.");
        });
    }
}
