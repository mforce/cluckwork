namespace Cluckwork.Application.Features.Inventory.RecordFeedUsage;

using Cluckwork.Domain.Inventory;
using FluentValidation;

public sealed class RecordFeedUsageValidator : AbstractValidator<RecordFeedUsageCommand>
{
    public RecordFeedUsageValidator()
    {
        RuleFor(x => x.FlockId).NotEmpty().WithErrorCode("FeedUsage.FlockId.Required");
        RuleFor(x => x.InventoryItemId).NotEmpty().WithErrorCode("FeedUsage.InventoryItemId.Required");
        // An omitted JSON date binds as default (0001-01-01).
        RuleFor(x => x.Date).NotEmpty()
            .WithMessage("Usage date is required.").WithErrorCode("FeedUsage.Date.Required");
        RuleFor(x => x.Quantity)
            .GreaterThan(0).WithErrorCode("FeedUsage.Quantity.Positive")
            .LessThanOrEqualTo(1_000_000_000m).WithErrorCode("FeedUsage.Quantity.Max")
            .Must(q => decimal.Round(q, 3) == q)
            .WithMessage("Quantity supports at most 3 decimal places.").WithErrorCode("FeedUsage.Quantity.Precision");
        RuleFor(x => x.Note)
            .MaximumLength(FeedUsage.MaxNoteLength).WithErrorCode("FeedUsage.Note.MaxLength");
    }
}
