namespace Cluckwork.Application.Features.Flocks;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Flocks;

public interface IBirdMovementRepository : IRepository<BirdMovement, Guid>
{
    // Newest first (date, then id) — ledger browsing.
    Task<IReadOnlyList<BirdMovement>> ListByFlockAsync(
        Guid flockId, int limit, int offset, CancellationToken ct = default);

    // Net birds removed for the given flocks only (Σ Quantity), one grouped
    // query (#512 T044). The id list is the page's flock ids, NOT the whole
    // account: an unbounded GROUP BY costs grow with the farm's all-time
    // movement count on every list request, which is the same defect #311 closed
    // in the report path. Flocks with no movements are absent from the result.
    Task<Dictionary<Guid, long>> RemovedForFlocksAsync(
        IReadOnlyCollection<Guid> flockIds, CancellationToken ct = default);

    Task<long> RemovedForFlockAsync(Guid flockId, CancellationToken ct = default);
}
