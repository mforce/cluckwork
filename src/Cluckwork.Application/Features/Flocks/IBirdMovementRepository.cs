namespace Cluckwork.Application.Features.Flocks;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Flocks;

public interface IBirdMovementRepository : IRepository<BirdMovement, Guid>
{
    // Newest first (date, then id) — ledger browsing.
    Task<IReadOnlyList<BirdMovement>> ListByFlockAsync(
        Guid flockId, int limit, int offset, CancellationToken ct = default);

    // Net birds removed per flock (Σ Quantity), one grouped query for lists.
    // Flocks with no movements are absent from the result.
    Task<Dictionary<Guid, long>> RemovedByFlockAsync(CancellationToken ct = default);

    Task<long> RemovedForFlockAsync(Guid flockId, CancellationToken ct = default);
}
