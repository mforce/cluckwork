namespace Cluckwork.Infrastructure.Repositories;

using Cluckwork.Application.Features.Inventory;
using Cluckwork.Domain.Inventory;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed class InventoryItemRepository(AppDbContext db) : IInventoryItemRepository
{
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

    // FOR UPDATE + fresh load, inside an open transaction. Tenant scoping is
    // the caller's job (handlers check AccountId), like the other locked reads.
    public Task<InventoryItem?> GetByIdLockedAsync(Guid id, CancellationToken ct = default) =>
        db.InventoryItems.FromSqlInterpolated($"""
            SELECT * FROM "InventoryItems" WHERE "Id" = {id} FOR UPDATE
            """)
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(ct);

    public async Task AddAsync(InventoryItem entity, CancellationToken ct = default) =>
        await db.InventoryItems.AddAsync(entity, ct);

    public void Update(InventoryItem entity) => db.InventoryItems.Update(entity);

    public void Remove(InventoryItem entity) => db.InventoryItems.Remove(entity);
}
