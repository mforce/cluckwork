using Cluckwork.Application.Modules.Farm.Accounts;
using Cluckwork.Application.Modules.GeneralInventory.Inventory;
using Cluckwork.Domain.Modules.GeneralInventory.Inventory;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Modules.GeneralInventory.Repositories;

public sealed class InventoryItemRepository(AppDbContext db) : IInventoryItemRepository,
    ICurrencyBoundRowSource
{
    // An item's default cost is what a purchase falls back to when no cost is
    // given, which would stamp a new lot in the old currency after a change.
    Task<bool> ICurrencyBoundRowSource.AnyAsync(CancellationToken ct) =>
        db.InventoryItems.AnyAsync(i => i.DefaultUnitCost != null, ct);

    public Task<InventoryItem?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.InventoryItems.FirstOrDefaultAsync(i => i.Id == id, ct);

    public async Task<IReadOnlyList<InventoryItem>> ListAsync(
        bool includeInactive = false, CancellationToken ct = default) =>
        await db.InventoryItems
            .AsNoTracking()
            .Where(i => includeInactive || i.Active)
            .OrderBy(i => i.Name)
            .ToListAsync(ct);

    public Task<bool> NameExistsAsync(
        Guid farmId, string name, Guid? excludeId = null, CancellationToken ct = default)
    {
        var normalized = name.Trim().ToLower();
        return db.InventoryItems.AnyAsync(
            i => i.FarmId == farmId
                 && i.Name.ToLower() == normalized
                 && (excludeId == null || i.Id != excludeId),
            ct);
    }

    public Task<bool> HasLotsAsync(Guid itemId, CancellationToken ct = default) =>
        db.InventoryLots.AnyAsync(l => l.InventoryItemId == itemId, ct);

    // FOR UPDATE + fresh load, inside an open transaction. AccountId is IN
    // the predicate (#313): a foreign-tenant id matches no row here, so
    // FOR UPDATE is never attempted against it. The caller's post-load
    // AccountId check stays in place as defense in depth.
    public Task<InventoryItem?> GetByIdLockedAsync(Guid accountId, Guid id, CancellationToken ct = default) =>
        db.InventoryItems.FromSqlInterpolated($"""
            SELECT * FROM "InventoryItems" WHERE "Id" = {id} AND "AccountId" = {accountId} FOR UPDATE
            """)
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(ct);

    public async Task AddAsync(InventoryItem entity, CancellationToken ct = default) =>
        await db.InventoryItems.AddAsync(entity, ct);
}
