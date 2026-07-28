namespace Cluckwork.Application.Features.DailyEntries.RecordDailyEntry;

using Cluckwork.Application.Common;
using FluentValidation;

public sealed class RecordDailyEntryValidator : AbstractValidator<RecordDailyEntryCommand>
{
    public RecordDailyEntryValidator(IFarmClock farmClock)
    {
        RuleFor(x => x.FarmId).NotEmpty().WithErrorCode("DailyEntry.FarmId.Required");
        RuleFor(x => x.HouseId).NotEmpty().WithErrorCode("DailyEntry.HouseId.Required");
        RuleFor(x => x.FlockId).NotEmpty().WithErrorCode("DailyEntry.FlockId.Required");
        // #35: the boundary is the farm's own today. The old rule compared
        // against UTC today + 1 day of slack, which was a stopgap for farms
        // ahead of UTC — it let a farm behind UTC post a genuinely future day.
        RuleFor(x => x.Date)
            .NotEmpty()
            .WithErrorCode("DailyEntry.Date.Required")
            .MustAsync(async (date, ct) => date <= await farmClock.TodayAsync(ct))
            .WithMessage("Entry date cannot be in the future.")
            .WithErrorCode("DailyEntry.Date.NotFuture");
        RuleFor(x => x.TotalEggs).GreaterThanOrEqualTo(0).WithErrorCode("DailyEntry.TotalEggs.NonNegative");
        RuleFor(x => x.CrackedEggs).GreaterThanOrEqualTo(0).WithErrorCode("DailyEntry.CrackedEggs.NonNegative");
        RuleFor(x => x.DirtyEggs).GreaterThanOrEqualTo(0).WithErrorCode("DailyEntry.DirtyEggs.NonNegative");
        RuleFor(x => x.DiscardedEggs).GreaterThanOrEqualTo(0).WithErrorCode("DailyEntry.DiscardedEggs.NonNegative");
        RuleFor(x => x.MortalityCount).GreaterThanOrEqualTo(0).WithErrorCode("DailyEntry.MortalityCount.NonNegative");
        // long accumulation — three ints can overflow past a positive total
        // (same fix as the adjust validator, codex review of PR #80).
        RuleFor(x => x)
            .Must(x => (long)x.CrackedEggs + x.DirtyEggs + x.DiscardedEggs <= x.TotalEggs)
            .WithName("Eggs")
            .WithMessage("Cracked + dirty + discarded cannot exceed total eggs.")
            .WithErrorCode("DailyEntry.Eggs.SumWithinTotal");

        When(x => x.Grades is { Count: > 0 }, () =>
        {
            RuleForEach(x => x.Grades!).ChildRules(g =>
            {
                g.RuleFor(x => x.EggGradeId).NotEmpty().WithErrorCode("DailyEntry.GradeEggGradeId.Required");
                g.RuleFor(x => x.Quantity).GreaterThan(0).WithErrorCode("DailyEntry.GradeQuantity.Positive");
            });

            RuleFor(x => x.Grades!)
                .Must(grades => grades
                    .Select(g => g.EggGradeId)
                    .Distinct()
                    .Count() == grades.Count)
                .WithName("Grades")
                .WithMessage("Each grade may appear only once.")
                .WithErrorCode("DailyEntry.Grades.Unique");

            // Grades are the sellable portion of the same total the losses come out
            // of; long accumulation so a pathological payload can't overflow Sum.
            RuleFor(x => x)
                .Must(x => x.Grades!.Sum(g => (long)g.Quantity)
                    <= (long)x.TotalEggs - x.CrackedEggs - x.DirtyEggs - x.DiscardedEggs)
                .WithName("Grades")
                .WithMessage("Graded quantities cannot exceed total eggs minus cracked/dirty/discarded.")
                .WithErrorCode("DailyEntry.Grades.WithinAvailable");
        });
    }
}
