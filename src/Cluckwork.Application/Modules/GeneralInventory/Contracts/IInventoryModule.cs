using Cluckwork.Domain.Common;
using Cluckwork.Domain.Modules.GeneralInventory.Inventory;

namespace Cluckwork.Application.Modules.GeneralInventory.Contracts;

// #855: the General Inventory contract. Adapters reach General Inventory only
// through the types marked [ModuleContract("GeneralInventory")].
public interface IInventoryModule
{
    Task<IReadOnlyList<InventoryItemDetails>> ListItemsAsync(bool includeInactive, CancellationToken ct);

    Task<InventoryItemDetails?> GetItemAsync(Guid id, CancellationToken ct);

    Task<Result<Guid>> CreateItemAsync(CreateInventoryItemCommand command, Guid accountId, CancellationToken ct);

    Task<Result> UpdateItemAsync(UpdateInventoryItemCommand command, Guid accountId, CancellationToken ct);

    Task<Result> SetItemActiveAsync(Guid id, bool active, CancellationToken ct);

    Task<Result<Guid>> RecordPurchaseAsync(RecordPurchaseCommand command, Guid accountId, CancellationToken ct);

    Task<IReadOnlyList<InventoryLotDetails>> ListLotsAsync(Guid itemId, CancellationToken ct);

    Task<IReadOnlyList<InventoryMovementDetails>> ListMovementsAsync(
        Guid itemId, int limit, int offset, CancellationToken ct);

    Task<Result<Guid>> RecordAdjustmentAsync(RecordAdjustmentCommand command, Guid accountId, CancellationToken ct);

    Task<Result<RecordFeedUsageResponse>> RecordFeedUsageAsync(
        RecordFeedUsageCommand command, Guid accountId, CancellationToken ct);

    Task<IReadOnlyList<FeedUsageDetails>> ListFeedUsageAsync(
        Guid? flockId, DateOnly? from, DateOnly? to, int limit, int offset, CancellationToken ct);

    Task<Result<Guid>> RecordWaterUsageAsync(RecordWaterUsageCommand command, Guid accountId, CancellationToken ct);

    Task<Result> UpdateWaterUsageAsync(UpdateWaterUsageCommand command, CancellationToken ct);

    Task<IReadOnlyList<WaterUsageDetails>> ListWaterUsageAsync(
        Guid? flockId, DateOnly? from, DateOnly? to, int limit, int offset, CancellationToken ct);
}

public sealed record InventoryItemDetails(
    Guid Id, Guid FarmId, string Name, InventoryCategory Category, string Unit,
    Money? DefaultUnitCost, decimal QuantityOnHand, bool Active, int Version);

public sealed record InventoryLotDetails(
    Guid Id, Guid InventoryItemId, DateOnly ReceivedDate, string? LotNumber, DateOnly? ExpiryDate,
    decimal QuantityReceived, decimal QuantityAvailable, Money UnitCost, int Version);

public sealed record InventoryMovementDetails(
    Guid Id, Guid InventoryItemId, Guid? InventoryLotId, DateOnly Date, InventoryMovementType Type,
    decimal QuantityDelta, string Unit, Guid? FlockId, string? Note, string? ReferenceType, Guid? ReferenceId);

public sealed record FeedUsageDetails(
    Guid Id, Guid FlockId, Guid InventoryItemId, DateOnly Date, decimal Quantity, string Unit,
    Money EstimatedCost, string? Note, Guid? DailyEntryId, int Version);

public sealed record WaterUsageDetails(
    Guid Id, Guid FlockId, DateOnly Date, decimal Quantity, string Unit, WaterSource Source,
    decimal? MeterStart, decimal? MeterEnd, string? Note, Guid? DailyEntryId, int Version);
