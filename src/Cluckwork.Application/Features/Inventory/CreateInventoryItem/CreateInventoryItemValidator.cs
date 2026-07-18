namespace Cluckwork.Application.Features.Inventory.CreateInventoryItem;

using Cluckwork.Domain.Inventory;
using FluentValidation;

public sealed class CreateInventoryItemValidator : AbstractValidator<CreateInventoryItemCommand>
{
    public CreateInventoryItemValidator()
    {
        // NotEmpty alone passes whitespace-only strings.
        RuleFor(x => x.Name)
            .Must(n => !string.IsNullOrWhiteSpace(n)).WithMessage("Item name is required.")
            .MaximumLength(InventoryItem.MaxNameLength);
        RuleFor(x => x.Unit)
            .Must(u => !string.IsNullOrWhiteSpace(u)).WithMessage("Unit is required.")
            .MaximumLength(InventoryItem.MaxUnitLength);
        // IsDefined too: TryParse accepts any numeric string.
        RuleFor(x => x.Category)
            .Must(c => Enum.TryParse<InventoryCategory>(c, ignoreCase: true, out var parsed)
                       && Enum.IsDefined(parsed))
            .WithMessage("Unknown inventory category.");
        RuleFor(x => x.DefaultUnitCostMinorUnits)
            .GreaterThanOrEqualTo(0).When(x => x.DefaultUnitCostMinorUnits is not null);
    }
}
