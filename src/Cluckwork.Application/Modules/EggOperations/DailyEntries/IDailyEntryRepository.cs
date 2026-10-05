using Cluckwork.Domain.Modules.EggOperations.Eggs;

namespace Cluckwork.Application.Modules.EggOperations.DailyEntries;

public interface IDailyEntryRepository
{
    Task<DailyEntry?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task AddAsync(DailyEntry entity, CancellationToken ct = default);

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

    // Tracked: the lock sweep (#69) locks what this returns.
    Task<IReadOnlyList<DailyEntry>> ListSubmittedBeforeAsync(
        DateOnly before, int limit, CancellationToken ct = default);

    // Write-side natural-key lookup (#388). Same bypass shape as
    // GetByIdForFlockScopedWriteAsync: IgnoreQueryFilters, AccountId
    // reinstated explicitly, natural key + non-Voided predicate preserved.
    // Post-live-guard write/provenance lookup only — read endpoints never
    // call it.
    Task<DailyEntry?> FindByNaturalKeyForFlockScopedWriteAsync(
        Guid accountId, Guid farmId, Guid houseId, Guid flockId, DateOnly date,
        CancellationToken ct = default);
}
