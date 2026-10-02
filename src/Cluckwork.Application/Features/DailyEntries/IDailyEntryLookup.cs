namespace Cluckwork.Application.Features.DailyEntries;

// #853: Egg Operations' daily-entry read port for peer modules.
public interface IDailyEntryLookup
{
    // The live (non-Voided) entry for this day, for a write that has already
    // passed IFlockScopeGuard; see
    // IDailyEntryRepository.FindByNaturalKeyForFlockScopedWriteAsync.
    Task<Guid?> FindIdForFlockScopedWriteAsync(
        Guid accountId, Guid farmId, Guid houseId, Guid flockId, DateOnly date, CancellationToken ct);
}
