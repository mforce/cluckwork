namespace Cluckwork.Domain.Inventory;

// General inventory catalog entry (spec §12.1) — feed-first for Phase 1.1
// (#66), but the category axis covers the later medication/vaccine/etc.
// slices so the schema doesn't churn. Quantities live on lots; an item is the
// definition (what it is, how it's measured, what it usually costs).
public sealed class InventoryItem : AggregateRoot<Guid>
{
    public const int MaxNameLength = 200;
    public const int MaxUnitLength = 20;

    public Guid FarmId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public InventoryCategory Category { get; private set; }

    // Unit of measure for every quantity of this item (lots, movements,
    // usage): "kg", "bags", "L"… Changing it is handler-guarded: allowed only
    // while no lots exist, or historical quantities would silently reinterpret.
    public string Unit { get; private set; } = string.Empty;

    // Default purchase cost per unit, prefilled into new lots. Null = unknown.
    public Money? DefaultUnitCost { get; private set; }

    public bool Active { get; private set; }
    public int Version { get; private set; }

    private InventoryItem() { }

    public static InventoryItem Create(
        Guid id, Guid accountId, Guid farmId, string name,
        InventoryCategory category, string unit, Money? defaultUnitCost)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Item name is required.", nameof(name));
        if (name.Trim().Length > MaxNameLength)
            throw new ArgumentException($"Item name cannot exceed {MaxNameLength} characters.", nameof(name));
        if (string.IsNullOrWhiteSpace(unit))
            throw new ArgumentException("Unit is required.", nameof(unit));
        if (unit.Trim().Length > MaxUnitLength)
            throw new ArgumentException($"Unit cannot exceed {MaxUnitLength} characters.", nameof(unit));
        if (defaultUnitCost is { IsNegative: true })
            throw new ArgumentException("Default unit cost cannot be negative.", nameof(defaultUnitCost));

        return new InventoryItem
        {
            Id = id, AccountId = accountId, FarmId = farmId,
            Name = name.Trim(),
            Category = category,
            Unit = unit.Trim(),
            DefaultUnitCost = defaultUnitCost,
            Active = true
        };
    }

    // Category stays immutable after creation (same rationale as EggGrade's
    // GradeType): history recorded under a category keeps it.
    public Result Update(string name, string unit, Money? defaultUnitCost)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure(Error.Validation("InventoryItem.NameRequired", "Item name is required."));
        if (name.Trim().Length > MaxNameLength)
            return Result.Failure(Error.Validation(
                "InventoryItem.NameTooLong", $"Item name cannot exceed {MaxNameLength} characters."));
        if (string.IsNullOrWhiteSpace(unit))
            return Result.Failure(Error.Validation("InventoryItem.UnitRequired", "Unit is required."));
        if (unit.Trim().Length > MaxUnitLength)
            return Result.Failure(Error.Validation(
                "InventoryItem.UnitTooLong", $"Unit cannot exceed {MaxUnitLength} characters."));
        if (defaultUnitCost is { IsNegative: true })
            return Result.Failure(Error.Validation(
                "InventoryItem.NegativeCost", "Default unit cost cannot be negative."));

        Name = name.Trim();
        Unit = unit.Trim();
        DefaultUnitCost = defaultUnitCost;
        Version++;
        return Result.Success();
    }

    public Result Deactivate()
    {
        if (!Active)
            return Result.Failure(Error.Domain("InventoryItem.NotActive", "Item is already inactive."));
        Active = false;
        Version++;
        return Result.Success();
    }

    public Result Activate()
    {
        if (Active)
            return Result.Failure(Error.Domain("InventoryItem.AlreadyActive", "Item is already active."));
        Active = true;
        Version++;
        return Result.Success();
    }
}

// Spec §12.1 category enum, in full — only Feed gets UI in Phase 1.1, the
// rest arrive with their phases (medication §13, packaging, …).
public enum InventoryCategory
{
    Feed, Supplement, Additive, Medication, Vaccine,
    Packaging, Bedding, Sanitation, EquipmentPart, Other
}
