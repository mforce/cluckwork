using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Eggs;
using Cluckwork.Domain.Eggs;

namespace Cluckwork.Application.Features.EggLots;

public sealed class EggStock(
    IEggLotRepository lots, IEggInventoryMovementRepository movements, ICurrentTransaction transaction) : IEggStock
{
    public async Task<IEggStockReservation> LockForSaleAsync(
        Guid accountId, IReadOnlyList<Guid> eggGradeIds, DateOnly saleDate, CancellationToken ct)
    {
        var lockedIn = RequireTransaction();
        return new Reservation(accountId, saleDate,
            await lots.GetAvailableFifoLockedAsync(accountId, eggGradeIds, saleDate, ct), movements,
            transaction, lockedIn);
    }

    public async Task<Result> RestoreAsync(
        Guid accountId, IReadOnlyDictionary<Guid, int> quantityByLot,
        string referenceType, Guid referenceId, string reason, CancellationToken ct)
    {
        RequireTransaction();
        var lockedLots = await lots.GetByIdsLockedAsync(accountId, quantityByLot.Keys.ToList(), ct);
        if (lockedLots.Count != quantityByLot.Count)
            return Result.Failure(Error.Domain(
                "EggLot.AllocationSourceMissing",
                "One or more source egg lots for this order no longer exist."));

        foreach (var lot in lockedLots)
        {
            var restore = lot.Restore(quantityByLot[lot.Id]);
            if (restore.IsFailure) return restore;

            // Ledger row (#101): the returned eggs re-enter as an explicit
            // Void movement, same transaction as the restore.
            await movements.AddAsync(EggInventoryMovement.Create(
                Guid.NewGuid(), accountId, lot.Id, EggMovementType.Void,
                quantityByLot[lot.Id], referenceType, referenceId, reason: reason), ct);
        }
        return Result.Success();
    }

    private Guid RequireTransaction() =>
        transaction.Id ?? throw new InvalidOperationException(
            "IEggStock runs only inside a transaction: its FOR UPDATE locks end with it.");

    private sealed class Reservation(
        Guid accountId, DateOnly saleDate, IReadOnlyList<EggLot> lockedLots,
        IEggInventoryMovementRepository movements, ICurrentTransaction transaction, Guid lockedIn)
        : IEggStockReservation
    {
        private readonly Dictionary<Guid, EggLot> _lotsById = lockedLots.ToDictionary(l => l.Id);

        public SaleAllocationPlan Plan(IReadOnlyList<SaleDemandLine> lines, IReadOnlySet<Guid>? fromFlocks = null)
        {
            EnsureLocked();
            return SaleAllocationPlanner.Plan(lines,
                fromFlocks is null ? lockedLots : lockedLots.Where(l => fromFlocks.Contains(l.FlockId)).ToList());
        }

        public async Task DrawAsync(
            PlannedEggLotDraw draw, string referenceType, Guid referenceId, CancellationToken ct)
        {
            EnsureLocked();
            var lot = _lotsById[draw.EggLotId];
            var allocated = lot.Allocate(draw.Quantity, saleDate);
            if (allocated.IsFailure)
                throw new InvalidOperationException(
                    $"Sale allocation plan contradicted EggLot.Allocate for lot {lot.Id}: " +
                    allocated.Error.Description);

            // Ledger row (#101): the draw leaves the lot as an explicit Sale
            // movement. It references the caller's allocation, not the order,
            // so two same-grade lines drawing from one lot stay distinguishable
            // (codex #102).
            await movements.AddAsync(EggInventoryMovement.Create(
                Guid.NewGuid(), accountId, lot.Id, EggMovementType.Sale,
                -draw.Quantity, referenceType, referenceId), ct);
        }

        // After the locking transaction ends, another one may already have
        // changed these lots, so neither the plan nor a draw can be trusted.
        private void EnsureLocked()
        {
            if (transaction.Id != lockedIn)
                throw new InvalidOperationException(
                    "This reservation's transaction has ended, so its lots are no longer locked.");
        }
    }
}
