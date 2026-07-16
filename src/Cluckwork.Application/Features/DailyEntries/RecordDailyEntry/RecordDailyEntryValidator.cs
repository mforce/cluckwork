namespace Cluckwork.Application.Features.DailyEntries.RecordDailyEntry;

using FluentValidation;

public sealed class RecordDailyEntryValidator : AbstractValidator<RecordDailyEntryCommand>
{
    public RecordDailyEntryValidator()
    {
        RuleFor(x => x.FarmId).NotEmpty();
        RuleFor(x => x.HouseId).NotEmpty();
        RuleFor(x => x.FlockId).NotEmpty();
        RuleFor(x => x.Date).NotEmpty();
        RuleFor(x => x.TotalEggs).GreaterThanOrEqualTo(0);
        RuleFor(x => x.CrackedEggs).GreaterThanOrEqualTo(0);
        RuleFor(x => x.DirtyEggs).GreaterThanOrEqualTo(0);
        RuleFor(x => x.DiscardedEggs).GreaterThanOrEqualTo(0);
        RuleFor(x => x.MortalityCount).GreaterThanOrEqualTo(0);
        RuleFor(x => x)
            .Must(x => x.CrackedEggs + x.DirtyEggs + x.DiscardedEggs <= x.TotalEggs)
            .WithName("Eggs")
            .WithMessage("Cracked + dirty + discarded cannot exceed total eggs.");

        When(x => x.Grades is { Count: > 0 }, () =>
        {
            RuleForEach(x => x.Grades!).ChildRules(g =>
            {
                g.RuleFor(x => x.GradeCode).NotEmpty().MaximumLength(20);
                g.RuleFor(x => x.Quantity).GreaterThan(0);
            });

            RuleFor(x => x.Grades!)
                .Must(grades => grades
                    .Select(g => g.GradeCode)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count() == grades.Count)
                .WithName("Grades")
                .WithMessage("Each grade may appear only once.");

            RuleFor(x => x)
                .Must(x => x.Grades!.Sum(g => g.Quantity) <= x.TotalEggs)
                .WithName("Grades")
                .WithMessage("Graded quantities cannot exceed total eggs.");
        });
    }
}
