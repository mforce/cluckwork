using Cluckwork.Application.Modules.EggOperations.Contracts;
using Cluckwork.Domain.Modules.EggOperations.Eggs;
using FluentValidation;

namespace Cluckwork.Application.Modules.EggOperations.DailyEntries.VoidDailyEntry;

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
