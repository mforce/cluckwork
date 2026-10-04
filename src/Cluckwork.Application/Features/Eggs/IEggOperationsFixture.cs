namespace Cluckwork.Application.Features.Eggs;

// Simulation fixture reads; registered beside the seeders outside Production.
public interface IEggOperationsFixture
{
    // Ignores the tenant filter; the seeder calls it before resolving the tenant.
    Task<IReadOnlyList<string>> ListSaleableGradeNamesAsync(Guid accountId, CancellationToken ct = default);

    Task<bool> DefaultHouseEntryExistsAsync(
        Guid accountId, Guid flockId, DateOnly date, CancellationToken ct = default);

    Task<EggOperationsFixtureCounts> CountAsync(CancellationToken ct = default);
}

public sealed record EggOperationsFixtureCounts(
    int DailyEntries,
    int DraftEntries,
    int SubmittedEntries,
    int LockedEntries,
    int EggLots);
