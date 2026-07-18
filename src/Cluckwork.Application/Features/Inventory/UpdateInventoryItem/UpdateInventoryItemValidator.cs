namespace Cluckwork.Application.Features.Inventory.UpdateInventoryItem;

using Cluckwork.Domain.Inventory;
using FluentValidation;

public sealed class UpdateInventoryItemValidator : AbstractValidator<UpdateInventoryItemCommand>
{
    public UpdateInventoryItemValidator()
    {
        RuleFor(x => x.InventoryItemId).NotEmpty();
        RuleFor(x => x.Name)
            .Must(n => !string.IsNullOrWhiteSpace(n)).WithMessage("Item name is required.")
            .MaximumLength(InventoryItem.MaxNameLength);
        RuleFor(x => x.Unit)
            .Must(u => !string.IsNullOrWhiteSpace(u)).WithMessage("Unit is required.")
            .MaximumLength(InventoryItem.MaxUnitLength);
        RuleFor(x => x.DefaultUnitCostMinorUnits)
            .GreaterThanOrEqualTo(0).When(x => x.DefaultUnitCostMinorUnits is not null)
            .LessThanOrEqualTo(10_000_000_000_000).When(x => x.DefaultUnitCostMinorUnits is not null);
    }
}
