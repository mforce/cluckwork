using Cluckwork.Application.Modules.GeneralInventory.Contracts;
using Cluckwork.Domain.Modules.GeneralInventory.Inventory;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Repositories;

// Reads rely on the tenant query filter (AccountId == current tenant).
public sealed class InventoryFixture(AppDbContext db) : IInventoryFixture
{
    public async Task<Guid?> FindItemIdByNameAsync(string name, CancellationToken ct = default) =>
        (await db.InventoryItems.FirstOrDefaultAsync(i => i.Name == name, ct))?.Id;

    // The seeder creates at most one lot per item, so any lot is the opening lot.
    public async Task<Guid?> FindLotIdAsync(Guid itemId, CancellationToken ct = default)
    {
        if (await db.InventoryLots.AnyAsync(l => l.InventoryItemId == itemId, ct))
            return (await db.InventoryLots.FirstAsync(l => l.InventoryItemId == itemId, ct)).Id;
        return null;
    }

    public Task<bool> HasAdjustmentOrDiscardAsync(Guid lotId, CancellationToken ct = default) =>
        db.InventoryMovements.AnyAsync(
            m => m.InventoryLotId == lotId
                 && (m.Type == InventoryMovementType.Adjustment || m.Type == InventoryMovementType.Discard), ct);

    public Task<bool> FeedLotMovementExistsAsync(Guid feedLotId, DateOnly date, CancellationToken ct = default) =>
        db.InventoryMovements.AnyAsync(m => m.InventoryLotId == feedLotId && m.Date == date, ct);

    public Task<int> CountFeedLotAdjustmentsAsync(Guid feedLotId, CancellationToken ct = default) =>
        db.InventoryMovements.CountAsync(
            m => m.InventoryLotId == feedLotId && m.Type == InventoryMovementType.Adjustment, ct);

    public Task<bool> FeedUsageExistsAsync(
        Guid flockId, Guid feedItemId, DateOnly date, CancellationToken ct = default) =>
        db.FeedUsages.AnyAsync(
            u => u.FlockId == flockId && u.InventoryItemId == feedItemId && u.Date == date, ct);

    public Task<bool> WaterUsageExistsAsync(Guid flockId, DateOnly date, CancellationToken ct = default) =>
        db.WaterUsages.AnyAsync(u => u.FlockId == flockId && u.Date == date, ct);

    public async Task<InventoryFixtureCounts> CountAsync(CancellationToken ct = default) =>
        new(
            Items: await db.InventoryItems.CountAsync(ct),
            Lots: await db.InventoryLots.CountAsync(ct),
            Movements: await db.InventoryMovements.CountAsync(ct),
            PurchaseMovements: await db.InventoryMovements.CountAsync(
                m => m.Type == InventoryMovementType.Purchase, ct),
            UsageMovements: await db.InventoryMovements.CountAsync(
                m => m.Type == InventoryMovementType.Usage, ct),
            AdjustmentMovements: await db.InventoryMovements.CountAsync(
                m => m.Type == InventoryMovementType.Adjustment, ct),
            DiscardMovements: await db.InventoryMovements.CountAsync(
                m => m.Type == InventoryMovementType.Discard, ct),
            FeedUsages: await db.FeedUsages.CountAsync(ct),
            WaterUsages: await db.WaterUsages.CountAsync(ct));
}
