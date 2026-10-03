namespace Cluckwork.Application.Features.EggLots;

// #854: Egg Operations' stock port for Commerce. It locks, plans and moves egg
// lots inside the caller's transaction and never saves, like IMortalityLedger,
// so a confirm or a void commits its lot, ledger and order rows together.
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

// The lots one LockForSaleAsync call locked.
public interface IEggStockReservation
{
    // Pure: plans over the locked lots, or only those from fromFlocks, and
    // plans the same way every time, so a caller can try assigned flocks first
    // and farm-wide second without a second lock.
    SaleAllocationPlan Plan(IReadOnlyList<SaleDemandLine> lines, IReadOnlySet<Guid>? fromFlocks = null);

    // Takes one planned draw from its locked lot and writes the Sale movement.
    // A draw the lot refuses means the plan was wrong, so it throws.
    Task DrawAsync(PlannedEggLotDraw draw, string referenceType, Guid referenceId, CancellationToken ct);
}
