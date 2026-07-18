namespace Cluckwork.Application.Features.Inventory.RecordPurchase;

using Cluckwork.Domain.Inventory;
using FluentValidation;

public sealed class RecordPurchaseValidator : AbstractValidator<RecordPurchaseCommand>
{
    public RecordPurchaseValidator()
    {
        RuleFor(x => x.InventoryItemId).NotEmpty();
        // An omitted JSON date binds as default (0001-01-01) and would pass the
        // handler's future-only check — reject it as missing input.
        RuleFor(x => x.ReceivedDate).NotEmpty()
            .WithMessage("Received date is required.");
        RuleFor(x => x.Quantity)
            .GreaterThan(0)
            // Far above any real receipt, far below numeric(18,3) overflow —
            // without a cap, Postgres rejects the insert as a misleading 409.
            .LessThanOrEqualTo(1_000_000_000m)
            // Storage is decimal(18,3); finer input would be silently rounded.
            .Must(q => decimal.Round(q, 3) == q)
            .WithMessage("Quantity supports at most 3 decimal places.");
        RuleFor(x => x.UnitCostMinorUnits)
            .GreaterThanOrEqualTo(0).When(x => x.UnitCostMinorUnits is not null)
            // Cap so quantity × cost (usage cost estimates) stays far inside
            // long range: 1e9 qty × 1e13 minor units < decimal precision loss
            // territory and > any real feed price by orders of magnitude.
            .LessThanOrEqualTo(10_000_000_000_000).When(x => x.UnitCostMinorUnits is not null);
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
