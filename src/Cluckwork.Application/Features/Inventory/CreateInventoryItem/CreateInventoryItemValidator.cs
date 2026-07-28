namespace Cluckwork.Application.Features.Inventory.CreateInventoryItem;

using Cluckwork.Domain.Inventory;
using FluentValidation;

public sealed class CreateInventoryItemValidator : AbstractValidator<CreateInventoryItemCommand>
{
    public CreateInventoryItemValidator()
    {
        // NotEmpty alone passes whitespace-only strings.
        RuleFor(x => x.Name)
            .Must(n => !string.IsNullOrWhiteSpace(n)).WithMessage("Item name is required.").WithErrorCode("InventoryItem.Name.Required")
            .MaximumLength(InventoryItem.MaxNameLength).WithErrorCode("InventoryItem.Name.MaxLength");
        RuleFor(x => x.Unit)
            .Must(u => !string.IsNullOrWhiteSpace(u)).WithMessage("Unit is required.").WithErrorCode("InventoryItem.Unit.Required")
            .MaximumLength(InventoryItem.MaxUnitLength).WithErrorCode("InventoryItem.Unit.MaxLength");
        // IsDefined too: TryParse accepts any numeric string.
        RuleFor(x => x.Category)
            .Must(c => Enum.TryParse<InventoryCategory>(c, ignoreCase: true, out var parsed)
                       && Enum.IsDefined(parsed))
            .WithMessage("Unknown inventory category.").WithErrorCode("InventoryItem.Category.Allowed");
        RuleFor(x => x.DefaultUnitCostMinorUnits)
            .GreaterThanOrEqualTo(0).WithErrorCode("InventoryItem.DefaultUnitCost.NonNegative").When(x => x.DefaultUnitCostMinorUnits is not null)
            .LessThanOrEqualTo(10_000_000_000_000).WithErrorCode("InventoryItem.DefaultUnitCost.Max").When(x => x.DefaultUnitCostMinorUnits is not null);
    }
}
