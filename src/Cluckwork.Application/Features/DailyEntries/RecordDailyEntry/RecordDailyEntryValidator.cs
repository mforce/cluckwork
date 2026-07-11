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
    }
}
