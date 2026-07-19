namespace Cluckwork.Application.Features.DailyEntries.VoidDailyEntry;

using Cluckwork.Domain.Eggs;
using FluentValidation;

public sealed class VoidDailyEntryValidator : AbstractValidator<VoidDailyEntryCommand>
{
    public VoidDailyEntryValidator()
    {
        RuleFor(x => x.DailyEntryId).NotEmpty();
        RuleFor(x => x.Version).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Reason)
            .Must(v => !string.IsNullOrWhiteSpace(v)).WithMessage("A reason is required.")
            .MaximumLength(DailyEntry.MaxReasonLength);
    }
}
