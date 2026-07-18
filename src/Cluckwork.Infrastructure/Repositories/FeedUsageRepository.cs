namespace Cluckwork.Infrastructure.Repositories;

using Cluckwork.Application.Features.Inventory;
using Cluckwork.Domain.Inventory;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed class FeedUsageRepository(AppDbContext db) : IFeedUsageRepository
{
    public Task<FeedUsage?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.FeedUsages.FirstOrDefaultAsync(u => u.Id == id, ct);

    public async Task<IReadOnlyList<FeedUsage>> ListAsync(
        Guid? flockId, DateOnly? from, DateOnly? to,
        int limit, int offset, CancellationToken ct = default) =>
        await db.FeedUsages
            .AsNoTracking()
            .Where(u => (flockId == null || u.FlockId == flockId)
                     && (from == null || u.Date >= from)
                     && (to == null || u.Date <= to))
            .OrderByDescending(u => u.Date).ThenByDescending(u => u.CreatedAtUtc).ThenByDescending(u => u.Id)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

    public async Task AddAsync(FeedUsage entity, CancellationToken ct = default) =>
        await db.FeedUsages.AddAsync(entity, ct);

    // Create-only: corrections are compensating inventory adjustments, never
    // edits of the usage record (it must stay attached to the movements it
    // generated).
    public void Update(FeedUsage entity) =>
        throw new NotSupportedException("Feed usage records are create-only; record an inventory Adjustment instead.");

    public void Remove(FeedUsage entity) =>
        throw new NotSupportedException("Feed usage records are create-only; record an inventory Adjustment instead.");
}
