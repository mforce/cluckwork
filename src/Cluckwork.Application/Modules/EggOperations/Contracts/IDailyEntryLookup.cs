namespace Cluckwork.Application.Modules.EggOperations.Contracts;

// #853: Egg Operations' daily-entry read port for peer modules.
[ModuleContract("EggOperations")]
public interface IDailyEntryLookup
{
    // The live (non-Voided) entry for this day. The caller must run
    // IFlockScopeGuard first: this read ignores both query filters and applies
    // only the AccountId it is given. See
    // IDailyEntryRepository.FindByNaturalKeyForFlockScopedWriteAsync.
    Task<Guid?> FindIdForFlockScopedWriteAsync(
        Guid accountId, Guid farmId, Guid houseId, Guid flockId, DateOnly date, CancellationToken ct);
}
