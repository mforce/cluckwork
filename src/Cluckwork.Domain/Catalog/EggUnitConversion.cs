namespace Cluckwork.Domain.Catalog;

using Cluckwork.Domain.Common;

// Spec §9.7 — eggs are stored as individual eggs, but products sell in packed
// units, and there is NO implicit fixed factor: a carton is 12, 18, or 30 eggs
// depending on the market. Each account carries one row per unit code; part 2
// resolves eggs-per-unit here at line creation and snapshots it on the sold
// line, so a later redefinition never reinterprets existing orders.
public sealed class EggUnitConversion : AggregateRoot<Guid>
{
    public EggUnit UnitCode { get; private set; }
    public int EggsPerUnit { get; private set; }
    public bool Active { get; private set; }
    public int Version { get; private set; }

    private EggUnitConversion() { }

    public static EggUnitConversion Create(Guid id, Guid accountId, EggUnit unitCode, int eggsPerUnit)
    {
        if (eggsPerUnit < 1)
            throw new ArgumentException("Eggs per unit must be at least 1.", nameof(eggsPerUnit));
        if (unitCode == EggUnit.Individual && eggsPerUnit != 1)
            throw new ArgumentException("The individual unit is always exactly 1 egg.", nameof(eggsPerUnit));

        return new EggUnitConversion
        {
            Id = id, AccountId = accountId,
            UnitCode = unitCode, EggsPerUnit = eggsPerUnit, Active = true
        };
    }

    public Result Update(int eggsPerUnit, bool active)
    {
        if (UnitCode == EggUnit.Individual)
            return Result.Failure(Error.Domain(
                "EggUnitConversion.IndividualImmutable",
                "The individual unit is always exactly 1 egg and cannot be changed."));
        if (eggsPerUnit < 1)
            return Result.Failure(Error.Validation(
                "EggUnitConversion.MinOne", "Eggs per unit must be at least 1."));

        EggsPerUnit = eggsPerUnit;
        Active = active;
        Version++;
        return Result.Success();
    }

    // Spec §9.7 suggested defaults — the farm confirms/overrides at setup
    // (case especially is market-specific).
    public static IReadOnlyList<EggUnitConversion> Defaults(Guid accountId) =>
    [
        Create(Guid.NewGuid(), accountId, EggUnit.Individual, 1),
        Create(Guid.NewGuid(), accountId, EggUnit.Dozen, 12),
        Create(Guid.NewGuid(), accountId, EggUnit.Flat, 30),
        Create(Guid.NewGuid(), accountId, EggUnit.Tray, 30),
        Create(Guid.NewGuid(), accountId, EggUnit.Carton, 12),
        Create(Guid.NewGuid(), accountId, EggUnit.Case, 360),
    ];
}

public enum EggUnit { Individual, Dozen, Flat, Tray, Carton, Case, Other }
