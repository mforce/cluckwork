namespace Cluckwork.Application.Features.Inventory.UpdateInventoryItem;

using Cluckwork.Domain.Inventory;
using FluentValidation;

public sealed class UpdateInventoryItemValidator : AbstractValidator<UpdateInventoryItemCommand>
{
    public UpdateInventoryItemValidator()
    {
        RuleFor(x => x.InventoryItemId).NotEmpty().WithErrorCode("InventoryItem.InventoryItemId.Required");
        RuleFor(x => x.Name)
            .Must(n => !string.IsNullOrWhiteSpace(n)).WithMessage("Item name is required.").WithErrorCode("InventoryItem.Name.Required")
            .MaximumLength(InventoryItem.MaxNameLength).WithErrorCode("InventoryItem.Name.MaxLength");
        RuleFor(x => x.Unit)
            .Must(u => !string.IsNullOrWhiteSpace(u)).WithMessage("Unit is required.").WithErrorCode("InventoryItem.Unit.Required")
            .MaximumLength(InventoryItem.MaxUnitLength).WithErrorCode("InventoryItem.Unit.MaxLength");
        RuleFor(x => x.DefaultUnitCostMinorUnits)
            .GreaterThanOrEqualTo(0).WithErrorCode("InventoryItem.DefaultUnitCost.NonNegative").When(x => x.DefaultUnitCostMinorUnits is not null)
            .LessThanOrEqualTo(10_000_000_000_000).WithErrorCode("InventoryItem.DefaultUnitCost.Max").When(x => x.DefaultUnitCostMinorUnits is not null);
    }
}
