namespace Cluckwork.Application.Features.EggLots.RecordEggLotMovement;

using Cluckwork.Domain.Eggs;
using FluentValidation;

public sealed class RecordEggLotMovementValidator : AbstractValidator<RecordEggLotMovementCommand>
{
    public RecordEggLotMovementValidator()
    {
        RuleFor(x => x.EggLotId).NotEmpty().WithErrorCode("EggLotMovement.EggLotId.Required");
        RuleFor(x => x.MovementType)
            .Must(t => t is nameof(EggMovementType.Discard)
                or nameof(EggMovementType.InternalUse)
                or nameof(EggMovementType.Reconciliation))
            .WithMessage("Type must be 'Discard', 'InternalUse' or 'Reconciliation'.")
            .WithErrorCode("EggLotMovement.MovementType.Allowed");
        RuleFor(x => x.QuantityDelta)
            .NotEqual(0).WithErrorCode("EggLotMovement.QuantityDelta.NonZero")
            .InclusiveBetween(-1_000_000_000, 1_000_000_000)
            .WithMessage("Quantity is out of range.").WithErrorCode("EggLotMovement.QuantityDelta.Range");
        // Only a reconciliation recount may add eggs back; a discard or
        // internal use always removes stock.
        RuleFor(x => x.QuantityDelta)
            .LessThan(0)
            .When(x => x.MovementType is nameof(EggMovementType.Discard) or nameof(EggMovementType.InternalUse))
            .WithMessage("Write-offs must be negative (stock leaves the farm).")
            .WithErrorCode("EggLotMovement.QuantityDelta.NegativeForWriteOff");
        // NotEmpty alone passes whitespace-only strings.
        RuleFor(x => x.Reason)
            .Must(r => !string.IsNullOrWhiteSpace(r))
            .WithMessage("A reason is required for corrections.").WithErrorCode("EggLotMovement.Reason.Required")
            .Must(r => r is null || r.Trim().Length <= EggInventoryMovement.MaxReasonLength)
            .WithMessage($"Reason must be at most {EggInventoryMovement.MaxReasonLength} characters.")
            .WithErrorCode("EggLotMovement.Reason.MaxLength");
    }
}
