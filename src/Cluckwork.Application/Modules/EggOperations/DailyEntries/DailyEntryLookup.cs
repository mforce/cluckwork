using Cluckwork.Application.Modules.EggOperations.Contracts;

namespace Cluckwork.Application.Modules.EggOperations.DailyEntries;

public sealed class DailyEntryLookup(IDailyEntryRepository entries) : IDailyEntryLookup
{
    public async Task<Guid?> FindIdForFlockScopedWriteAsync(
        Guid accountId, Guid farmId, Guid houseId, Guid flockId, DateOnly date, CancellationToken ct) =>
        (await entries.FindByNaturalKeyForFlockScopedWriteAsync(accountId, farmId, houseId, flockId, date, ct))?.Id;
}
