namespace Cluckwork.Application.Modules.EggOperations.Contracts;

// Seed fixture reads and writes; registered beside the seeders outside Production.
[ModuleContract("EggOperations")]
public interface IEggOperationsFixture
{
    // Ignores the tenant filter; the seeder calls it before resolving the tenant.
    Task<IReadOnlyList<string>> ListSaleableGradeNamesAsync(Guid accountId, CancellationToken ct = default);

    Task<bool> DefaultHouseEntryExistsAsync(
        Guid accountId, Guid flockId, DateOnly date, CancellationToken ct = default);

    Task<EggOperationsFixtureCounts> CountAsync(CancellationToken ct = default);

    // Ignores the tenant filter; the demo seeder calls it before resolving the tenant.
    Task<bool> AnyGradeAsync(Guid accountId, CancellationToken ct = default);

    // Demo cleanup: deletes every row of the farm, ignoring the tenant filter.
    // Runs inside the caller's transaction and never commits.
    Task PurgeDailyEntriesAsync(Guid accountId, CancellationToken ct = default);
}

[ModuleContract("EggOperations")]
public sealed record EggOperationsFixtureCounts(
    int DailyEntries,
    int DraftEntries,
    int SubmittedEntries,
    int LockedEntries,
    int EggLots);
