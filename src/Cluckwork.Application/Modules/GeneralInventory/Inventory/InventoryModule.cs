using Cluckwork.Application.Modules.GeneralInventory.Contracts;
using Cluckwork.Application.Modules.GeneralInventory.Inventory.CreateInventoryItem;
using Cluckwork.Application.Modules.GeneralInventory.Inventory.RecordAdjustment;
using Cluckwork.Application.Modules.GeneralInventory.Inventory.RecordFeedUsage;
using Cluckwork.Application.Modules.GeneralInventory.Inventory.RecordPurchase;
using Cluckwork.Application.Modules.GeneralInventory.Inventory.RecordWaterUsage;
using Cluckwork.Application.Modules.GeneralInventory.Inventory.UpdateInventoryItem;
using Cluckwork.Application.Modules.GeneralInventory.Inventory.UpdateWaterUsage;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Modules.GeneralInventory.Inventory;

namespace Cluckwork.Application.Modules.GeneralInventory.Inventory;

public sealed class InventoryModule(
    IInventoryItemRepository items,
    IInventoryLotRepository lots,
    IInventoryMovementRepository movements,
    IFeedUsageRepository feedUsages,
    IWaterUsageRepository waterUsages,
    CreateInventoryItemHandler createItem,
    UpdateInventoryItemHandler updateItem,
    SetInventoryItemActiveHandler setItemActive,
    RecordPurchaseHandler recordPurchase,
    RecordAdjustmentHandler recordAdjustment,
    RecordFeedUsageHandler recordFeedUsage,
    RecordWaterUsageHandler recordWaterUsage,
    UpdateWaterUsageHandler updateWaterUsage) : IInventoryModule
{
    public async Task<IReadOnlyList<InventoryItemDetails>> ListItemsAsync(bool includeInactive, CancellationToken ct)
    {
        var list = await items.ListAsync(includeInactive, ct);
        var stock = await lots.StockByItemAsync(ct);
        return list.Select(i => ToDetails(i, stock.GetValueOrDefault(i.Id))).ToList();
    }

    public async Task<InventoryItemDetails?> GetItemAsync(Guid id, CancellationToken ct)
    {
        var item = await items.GetByIdAsync(id, ct);
        if (item is null) return null;
        var stock = await lots.StockByItemAsync(ct);
        return ToDetails(item, stock.GetValueOrDefault(item.Id));
    }

    public Task<Result<Guid>> CreateItemAsync(CreateInventoryItemCommand command, Guid accountId, CancellationToken ct) =>
        createItem.HandleAsync(command, accountId, ct);

    public Task<Result> UpdateItemAsync(UpdateInventoryItemCommand command, Guid accountId, CancellationToken ct) =>
        updateItem.HandleAsync(command, accountId, ct);

    public Task<Result> SetItemActiveAsync(Guid id, bool active, CancellationToken ct) =>
        setItemActive.HandleAsync(id, active, ct);

    public Task<Result<Guid>> RecordPurchaseAsync(RecordPurchaseCommand command, Guid accountId, CancellationToken ct) =>
        recordPurchase.HandleAsync(command, accountId, ct);

    public async Task<IReadOnlyList<InventoryLotDetails>> ListLotsAsync(Guid itemId, CancellationToken ct) =>
        (await lots.ListByItemAsync(itemId, ct)).Select(l => new InventoryLotDetails(
            l.Id, l.InventoryItemId, l.ReceivedDate, l.LotNumber, l.ExpiryDate,
            l.QuantityReceived, l.QuantityAvailable, l.UnitCost, l.Version)).ToList();

    public async Task<IReadOnlyList<InventoryMovementDetails>> ListMovementsAsync(
        Guid itemId, int limit, int offset, CancellationToken ct) =>
        (await movements.ListByItemAsync(itemId, limit, offset, ct)).Select(m => new InventoryMovementDetails(
            m.Id, m.InventoryItemId, m.InventoryLotId, m.Date, m.Type,
            m.QuantityDelta, m.Unit, m.FlockId, m.Note, m.ReferenceType, m.ReferenceId)).ToList();

    public Task<Result<Guid>> RecordAdjustmentAsync(RecordAdjustmentCommand command, Guid accountId, CancellationToken ct) =>
        recordAdjustment.HandleAsync(command, accountId, ct);

    public Task<Result<RecordFeedUsageResponse>> RecordFeedUsageAsync(
        RecordFeedUsageCommand command, Guid accountId, CancellationToken ct) =>
        recordFeedUsage.HandleAsync(command, accountId, ct);

    public async Task<IReadOnlyList<FeedUsageDetails>> ListFeedUsageAsync(
        Guid? flockId, DateOnly? from, DateOnly? to, int limit, int offset, CancellationToken ct) =>
        (await feedUsages.ListAsync(flockId, from, to, limit, offset, ct)).Select(u => new FeedUsageDetails(
            u.Id, u.FlockId, u.InventoryItemId, u.Date, u.Quantity, u.Unit,
            u.EstimatedCost, u.Note, u.DailyEntryId, u.Version)).ToList();

    public Task<Result<Guid>> RecordWaterUsageAsync(RecordWaterUsageCommand command, Guid accountId, CancellationToken ct) =>
        recordWaterUsage.HandleAsync(command, accountId, ct);

    public Task<Result> UpdateWaterUsageAsync(UpdateWaterUsageCommand command, CancellationToken ct) =>
        updateWaterUsage.HandleAsync(command, ct);

    public async Task<IReadOnlyList<WaterUsageDetails>> ListWaterUsageAsync(
        Guid? flockId, DateOnly? from, DateOnly? to, int limit, int offset, CancellationToken ct) =>
        (await waterUsages.ListAsync(flockId, from, to, limit, offset, ct)).Select(u => new WaterUsageDetails(
            u.Id, u.FlockId, u.Date, u.Quantity, u.Unit, u.Source,
            u.MeterStart, u.MeterEnd, u.Note, u.DailyEntryId, u.Version)).ToList();

    private static InventoryItemDetails ToDetails(InventoryItem i, decimal onHand) =>
        new(i.Id, i.FarmId, i.Name, i.Category, i.Unit, i.DefaultUnitCost, onHand, i.Active, i.Version);
}
