namespace Cluckwork.Application.Features.DailyEntries;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Eggs;

public interface IDailyEntryRepository : IRepository<DailyEntry, Guid>
{
    // Paged, newest-first, grades included. Optional flock/date filters.
    Task<IReadOnlyList<DailyEntry>> ListAsync(
        Guid? flockId, DateOnly? from, DateOnly? to, int limit, int offset,
        CancellationToken ct = default);

    Task<DailyEntry?> FindByNaturalKeyAsync(
        Guid accountId, Guid farmId, Guid houseId, Guid flockId, DateOnly date,
        CancellationToken ct = default);
}
