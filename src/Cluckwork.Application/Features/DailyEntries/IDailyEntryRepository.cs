namespace Cluckwork.Application.Features.DailyEntries;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Eggs;

public interface IDailyEntryRepository : IRepository<DailyEntry, Guid>
{
    // Untracked read for GET endpoints (the tracked GetByIdAsync is the write path).
    Task<DailyEntry?> GetReadOnlyAsync(Guid id, CancellationToken ct = default);

    // Write-side authorization lookup (#388). The implementation bypasses the
    // combined tenant+flock query filter, then reinstates AccountId explicitly:
    // an own-account unassigned draft must reach FlockScopeGuard and keep the
    // existing 422 FlockScope.NotAssigned contract, while a foreign-account id
    // remains 404. READ endpoints never call this method and stay symmetric 404.
    Task<DailyEntry?> GetByIdForFlockScopedWriteAsync(
        Guid id, Guid accountId, CancellationToken ct = default);

    // Paged, newest-first, grades included. Optional flock/date filters.
    Task<IReadOnlyList<DailyEntry>> ListAsync(
        Guid? flockId, DateOnly? from, DateOnly? to, int limit, int offset,
        CancellationToken ct = default);

    Task<DailyEntry?> FindByNaturalKeyAsync(
        Guid accountId, Guid farmId, Guid houseId, Guid flockId, DateOnly date,
        CancellationToken ct = default);
}
