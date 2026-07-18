namespace Cluckwork.Application.Features.Inventory;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Inventory;

public interface IInventoryLotRepository : IRepository<InventoryLot, Guid>
{
    // FIFO-ordered lots with stock remaining for one item, locked FOR UPDATE —
    // the feed-usage consumption path (PR2 of #66). Canonical (ReceivedDate,
    // Id) lock order, same discipline as egg-lot allocation. Call inside an
    // open transaction.
    Task<IReadOnlyList<InventoryLot>> GetAvailableFifoLockedAsync(
        Guid accountId, Guid inventoryItemId, CancellationToken ct = default);

    // Lot browsing for an item, newest received first.
    Task<IReadOnlyList<InventoryLot>> ListByItemAsync(
        Guid inventoryItemId, CancellationToken ct = default);

    // Single lot under FOR UPDATE for the adjustment path — call inside an
    // open transaction. Unlike the FIFO fetch this returns empty lots too
    // (a positive adjustment targets exactly those).
    Task<InventoryLot?> GetByIdLockedAsync(
        Guid accountId, Guid lotId, CancellationToken ct = default);

    // On-hand per item (Σ QuantityAvailable), one grouped query for the
    // catalog screen. Items with no lots are absent.
    Task<Dictionary<Guid, decimal>> StockByItemAsync(CancellationToken ct = default);
}
