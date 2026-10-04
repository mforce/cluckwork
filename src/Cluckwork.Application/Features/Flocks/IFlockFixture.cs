namespace Cluckwork.Application.Features.Flocks;

// Simulation fixture reads; registered beside the seeders outside Production.
public interface IFlockFixture
{
    Task<bool> BirdMovementExistsAsync(Guid flockId, DateOnly date, CancellationToken ct = default);

    Task<int> CountAdjustmentsAsync(Guid flockId, CancellationToken ct = default);

    Task<FlockFixtureCounts> CountAsync(CancellationToken ct = default);
}

public sealed record FlockFixtureCounts(
    int Flocks,
    int ActiveFlocks,
    int DepletedFlocks,
    int ArchivedFlocks,
    int BirdMovements);
