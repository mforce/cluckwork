namespace Cluckwork.Application.Features.Inventory.RecordAdjustment;

using Cluckwork.Domain.Inventory;
using FluentValidation;

public sealed class RecordAdjustmentValidator : AbstractValidator<RecordAdjustmentCommand>
{
    public RecordAdjustmentValidator()
    {
        RuleFor(x => x.InventoryItemId).NotEmpty().WithErrorCode("InventoryAdjustment.InventoryItemId.Required");
        RuleFor(x => x.InventoryLotId).NotEmpty().WithErrorCode("InventoryAdjustment.InventoryLotId.Required");
        RuleFor(x => x.Date).NotEmpty()
            .WithMessage("Adjustment date is required.").WithErrorCode("InventoryAdjustment.Date.Required");
        RuleFor(x => x.Type)
            .Must(t => t is "Adjustment" or "Discard")
            .WithMessage("Type must be 'Adjustment' or 'Discard'.").WithErrorCode("InventoryAdjustment.Type.Allowed");
        RuleFor(x => x.QuantityDelta)
            .NotEqual(0).WithErrorCode("InventoryAdjustment.QuantityDelta.NonZero")
            .InclusiveBetween(-1_000_000_000m, 1_000_000_000m)
            .WithMessage("Quantity is out of range.").WithErrorCode("InventoryAdjustment.QuantityDelta.Range")
            .Must(q => decimal.Round(q, 3) == q)
            .WithMessage("Quantity supports at most 3 decimal places.").WithErrorCode("InventoryAdjustment.QuantityDelta.Precision");
        RuleFor(x => x.QuantityDelta)
            .LessThan(0)
            .When(x => x.Type == "Discard")
            .WithMessage("Discards must be negative (stock leaves the inventory).").WithErrorCode("InventoryAdjustment.QuantityDelta.NegativeForDiscard");
        // NotEmpty alone passes whitespace-only strings.
        RuleFor(x => x.Reason)
            .Must(r => !string.IsNullOrWhiteSpace(r))
            .WithMessage("A reason is required for corrections.").WithErrorCode("InventoryAdjustment.Reason.Required")
            .Must(r => r is null || r.Trim().Length <= InventoryMovement.MaxNoteLength)
            .WithMessage($"Reason must be at most {InventoryMovement.MaxNoteLength} characters.").WithErrorCode("InventoryAdjustment.Reason.MaxLength");
    }
}
