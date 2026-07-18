namespace Cluckwork.Application.Features.Inventory.RecordAdjustment;

using Cluckwork.Domain.Inventory;
using FluentValidation;

public sealed class RecordAdjustmentValidator : AbstractValidator<RecordAdjustmentCommand>
{
    public RecordAdjustmentValidator()
    {
        RuleFor(x => x.InventoryItemId).NotEmpty();
        RuleFor(x => x.InventoryLotId).NotEmpty();
        RuleFor(x => x.Date).NotEmpty()
            .WithMessage("Adjustment date is required.");
        RuleFor(x => x.Type)
            .Must(t => t is "Adjustment" or "Discard")
            .WithMessage("Type must be 'Adjustment' or 'Discard'.");
        RuleFor(x => x.QuantityDelta)
            .NotEqual(0)
            .InclusiveBetween(-1_000_000_000m, 1_000_000_000m)
            .WithMessage("Quantity is out of range.")
            .Must(q => decimal.Round(q, 3) == q)
            .WithMessage("Quantity supports at most 3 decimal places.");
        RuleFor(x => x.QuantityDelta)
            .LessThan(0)
            .When(x => x.Type == "Discard")
            .WithMessage("Discards must be negative (stock leaves the inventory).");
        // NotEmpty alone passes whitespace-only strings.
        RuleFor(x => x.Reason)
            .Must(r => !string.IsNullOrWhiteSpace(r))
            .WithMessage("A reason is required for corrections.")
            .Must(r => r is null || r.Trim().Length <= InventoryMovement.MaxNoteLength)
            .WithMessage($"Reason must be at most {InventoryMovement.MaxNoteLength} characters.");
    }
}
