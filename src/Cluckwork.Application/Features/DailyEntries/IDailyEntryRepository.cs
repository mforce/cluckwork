namespace Cluckwork.Application.Features.DailyEntries;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Eggs;

public interface IDailyEntryRepository : IRepository<DailyEntry, Guid>
{
    Task<DailyEntry?> FindByNaturalKeyAsync(
        Guid accountId, Guid farmId, Guid houseId, Guid flockId, DateOnly date,
        CancellationToken ct = default);
}
