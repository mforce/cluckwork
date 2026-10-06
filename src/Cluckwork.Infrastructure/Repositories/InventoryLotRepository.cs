using Cluckwork.Application.Modules.Farm.Accounts;
using Cluckwork.Application.Modules.GeneralInventory.Inventory;
using Cluckwork.Domain.Modules.GeneralInventory.Inventory;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Repositories;

public sealed class InventoryLotRepository(AppDbContext db) : IInventoryLotRepository,
    ICurrencyBoundRowSource
{
    // Every lot stores a unit cost; costs from two denominations would be summed
    // as though they were one.
    Task<bool> ICurrencyBoundRowSource.AnyAsync(CancellationToken ct) =>
        db.InventoryLots.AnyAsync(ct);

    // FOR UPDATE lock for FIFO feed-usage consumption. Canonical
    // (ReceivedDate, Id) ordering — every locking path over these rows must
    // share it (the egg-lot deadlock lesson from #60/PR #64).
    public async Task<IReadOnlyList<InventoryLot>> GetAvailableFifoLockedAsync(
        Guid accountId, Guid inventoryItemId, DateOnly asOfDate, CancellationToken ct = default)
    {
        return await db.InventoryLots.FromSqlInterpolated($"""
            SELECT *
            FROM "InventoryLots"
            WHERE "AccountId" = {accountId}
              AND "InventoryItemId" = {inventoryItemId}
              AND "QuantityAvailable" > 0
              AND "ReceivedDate" <= {asOfDate}
            ORDER BY "ReceivedDate", "Id"
            FOR UPDATE
            """)
            .IgnoreQueryFilters()
            .ToListAsync(ct);
    }

    // Single-lot FOR UPDATE for adjustments (empty lots included — positive
    // corrections target exactly those). Single-row lock: no ordering
    // interplay with the FIFO fetch to reason about beyond both being
    // row locks on this table.
    public Task<InventoryLot?> GetByIdLockedAsync(
        Guid accountId, Guid lotId, CancellationToken ct = default) =>
        db.InventoryLots.FromSqlInterpolated($"""
            SELECT *
            FROM "InventoryLots"
            WHERE "AccountId" = {accountId}
              AND "Id" = {lotId}
            FOR UPDATE
            """)
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(ct);

    public async Task<IReadOnlyList<InventoryLot>> ListByItemAsync(
        Guid inventoryItemId, CancellationToken ct = default) =>
        await db.InventoryLots
            .AsNoTracking()
            .Where(l => l.InventoryItemId == inventoryItemId)
            .OrderByBusinessChronologyDescending(l => l.ReceivedDate)
            .ToListAsync(ct);

    public async Task<Dictionary<Guid, decimal>> StockByItemAsync(CancellationToken ct = default) =>
        await db.InventoryLots
            .AsNoTracking()
            .GroupBy(l => l.InventoryItemId)
            .Select(g => new { ItemId = g.Key, OnHand = g.Sum(l => l.QuantityAvailable) })
            .ToDictionaryAsync(x => x.ItemId, x => x.OnHand, ct);

    public async Task AddAsync(InventoryLot entity, CancellationToken ct = default) =>
        await db.InventoryLots.AddAsync(entity, ct);
}
