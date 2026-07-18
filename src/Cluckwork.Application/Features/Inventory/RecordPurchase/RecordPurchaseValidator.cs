namespace Cluckwork.Application.Features.Inventory.RecordPurchase;

using Cluckwork.Domain.Inventory;
using FluentValidation;

public sealed class RecordPurchaseValidator : AbstractValidator<RecordPurchaseCommand>
{
    public RecordPurchaseValidator()
    {
        RuleFor(x => x.InventoryItemId).NotEmpty();
        RuleFor(x => x.Quantity)
            .GreaterThan(0)
            // Storage is decimal(18,3); finer input would be silently rounded.
            .Must(q => decimal.Round(q, 3) == q)
            .WithMessage("Quantity supports at most 3 decimal places.");
        RuleFor(x => x.UnitCostMinorUnits)
            .GreaterThanOrEqualTo(0).When(x => x.UnitCostMinorUnits is not null);
        RuleFor(x => x.LotNumber)
            .MaximumLength(InventoryLot.MaxLotNumberLength);
        RuleFor(x => x.ExpiryDate)
            .GreaterThanOrEqualTo(x => x.ReceivedDate)
            .When(x => x.ExpiryDate is not null)
            .WithMessage("Expiry date cannot precede the received date.");
        RuleFor(x => x.Note)
            .MaximumLength(InventoryMovement.MaxNoteLength);
    }
}
