namespace Cluckwork.Application.Modules.GeneralInventory.Contracts;

// Simulation fixture reads; registered beside the seeders outside Production.
[ModuleContract("GeneralInventory")]
public interface IInventoryFixture
{
    Task<Guid?> FindItemIdByNameAsync(string name, CancellationToken ct = default);

    Task<Guid?> FindLotIdAsync(Guid itemId, CancellationToken ct = default);

    Task<bool> HasAdjustmentOrDiscardAsync(Guid lotId, CancellationToken ct = default);

    Task<bool> FeedLotMovementExistsAsync(Guid feedLotId, DateOnly date, CancellationToken ct = default);

    Task<int> CountFeedLotAdjustmentsAsync(Guid feedLotId, CancellationToken ct = default);

    Task<bool> FeedUsageExistsAsync(Guid flockId, Guid feedItemId, DateOnly date, CancellationToken ct = default);

    Task<bool> WaterUsageExistsAsync(Guid flockId, DateOnly date, CancellationToken ct = default);

    Task<InventoryFixtureCounts> CountAsync(CancellationToken ct = default);
}

[ModuleContract("GeneralInventory")]
public sealed record InventoryFixtureCounts(
    int Items,
    int Lots,
    int Movements,
    int PurchaseMovements,
    int UsageMovements,
    int AdjustmentMovements,
    int DiscardMovements,
    int FeedUsages,
    int WaterUsages);
