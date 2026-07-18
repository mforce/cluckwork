namespace Cluckwork.Application.Features.Inventory.RecordFeedUsage;

using Cluckwork.Domain.Inventory;
using FluentValidation;

public sealed class RecordFeedUsageValidator : AbstractValidator<RecordFeedUsageCommand>
{
    public RecordFeedUsageValidator()
    {
        RuleFor(x => x.FlockId).NotEmpty();
        RuleFor(x => x.InventoryItemId).NotEmpty();
        // An omitted JSON date binds as default (0001-01-01).
        RuleFor(x => x.Date).NotEmpty()
            .WithMessage("Usage date is required.");
        RuleFor(x => x.Quantity)
            .GreaterThan(0)
            .LessThanOrEqualTo(1_000_000_000m)
            .Must(q => decimal.Round(q, 3) == q)
            .WithMessage("Quantity supports at most 3 decimal places.");
        RuleFor(x => x.Note)
            .MaximumLength(FeedUsage.MaxNoteLength);
    }
}
