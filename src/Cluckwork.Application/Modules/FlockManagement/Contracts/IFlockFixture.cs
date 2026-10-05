using Cluckwork.Domain.Common;

namespace Cluckwork.Application.Modules.FlockManagement.Contracts;

// Seed fixture reads and writes; registered beside the seeders outside Production.
public interface IFlockFixture
{
    Task<bool> BirdMovementExistsAsync(Guid flockId, DateOnly date, CancellationToken ct = default);

    Task<int> CountAdjustmentsAsync(Guid flockId, CancellationToken ct = default);

    Task<FlockFixtureCounts> CountAsync(CancellationToken ct = default);

    // Ignores the tenant filter; the demo seeder calls it before resolving the tenant.
    Task<bool> AnyFlockAsync(Guid accountId, CancellationToken ct = default);

    // Saves only when the domain accepts the depletion.
    Task<Result> DepleteAsync(Guid flockId, DateOnly asOf, CancellationToken ct = default);

    // Demo cleanup: deletes every row of the farm, ignoring the tenant filter.
    // Runs inside the caller's transaction and never commits. A bird movement
    // references its daily entry and its flock (both ON DELETE RESTRICT), so
    // this runs before Egg Operations' purge and PurgeFlocksAsync.
    Task PurgeBirdMovementsAsync(Guid accountId, CancellationToken ct = default);

    // Demo cleanup, as above.
    Task PurgeFlocksAsync(Guid accountId, CancellationToken ct = default);
}

public sealed record FlockFixtureCounts(
    int Flocks,
    int ActiveFlocks,
    int DepletedFlocks,
    int ArchivedFlocks,
    int BirdMovements);
