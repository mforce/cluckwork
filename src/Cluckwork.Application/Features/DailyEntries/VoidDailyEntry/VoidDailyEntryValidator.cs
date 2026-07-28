namespace Cluckwork.Application.Features.DailyEntries.VoidDailyEntry;

using Cluckwork.Domain.Eggs;
using FluentValidation;

public sealed class VoidDailyEntryValidator : AbstractValidator<VoidDailyEntryCommand>
{
    public VoidDailyEntryValidator()
    {
        RuleFor(x => x.DailyEntryId).NotEmpty().WithErrorCode("DailyEntry.DailyEntryId.Required");
        RuleFor(x => x.Version).GreaterThanOrEqualTo(0).WithErrorCode("DailyEntry.Version.NonNegative");
        RuleFor(x => x.Reason)
            .Must(v => !string.IsNullOrWhiteSpace(v)).WithMessage("A reason is required.")
            .WithErrorCode("DailyEntry.Reason.Required")
            .MaximumLength(DailyEntry.MaxReasonLength)
            .WithErrorCode("DailyEntry.Reason.MaxLength");
    }
}
