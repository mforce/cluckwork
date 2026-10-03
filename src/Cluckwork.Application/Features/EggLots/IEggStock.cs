namespace Cluckwork.Application.Features.EggLots;

// #854: Egg Operations' stock port for Commerce. It locks, plans and moves egg
// lots inside the caller's transaction and never saves, like IMortalityLedger,
// so a confirm or a void commits its lot, ledger and order rows together. It
// refuses to run outside a transaction.
public interface IEggStock
{
    // ONE FOR UPDATE over every available lot of these grades, farm-wide, in
    // (ProductionDate, Id) order (#612). Call it once per confirm.
    Task<IEggStockReservation> LockForSaleAsync(
        Guid accountId, IReadOnlyList<Guid> eggGradeIds, DateOnly saleDate, CancellationToken ct);

    // Locks the lots by id in (ProductionDate, Id) order, returns each lot's
    // quantity and writes one Void movement per lot.
    Task<Result> RestoreAsync(
        Guid accountId, IReadOnlyDictionary<Guid, int> quantityByLot,
        string referenceType, Guid referenceId, string reason, CancellationToken ct);
}

// The lots one LockForSaleAsync call locked. It works only while the
// transaction that locked them is current, and throws after it ends.
public interface IEggStockReservation
{
    // Plans over the locked lots' current availability, or only the lots from
    // fromFlocks, and changes nothing, so a caller can try assigned flocks first
    // and farm-wide second without a second lock. A DrawAsync lowers
    // availability, so a plan made after a draw sees less.
    SaleAllocationPlan Plan(IReadOnlyList<SaleDemandLine> lines, IReadOnlySet<Guid>? fromFlocks = null);

    // Takes one planned draw from its locked lot and writes the Sale movement.
    // The lot is the same locked instance the plan read, so a refusal means the
    // plan was wrong: it throws, and is never a 422.
    Task DrawAsync(PlannedEggLotDraw draw, string referenceType, Guid referenceId, CancellationToken ct);
}
