using Cluckwork.Application.Features.Accounts;
using Cluckwork.Application.Features.Inventory;
using Cluckwork.Domain.Inventory;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Repositories;

public sealed class FeedUsageRepository(AppDbContext db) : IFeedUsageRepository,
    ICurrencyBoundRowSource
{
    // Every usage stores an estimated cost.
    Task<bool> ICurrencyBoundRowSource.AnyAsync(CancellationToken ct) =>
        db.FeedUsages.AnyAsync(ct);

    public async Task<IReadOnlyList<FeedUsage>> ListAsync(
        Guid? flockId, DateOnly? from, DateOnly? to,
        int limit, int offset, CancellationToken ct = default) =>
        await db.FeedUsages
            .AsNoTracking()
            .Where(u => (flockId == null || u.FlockId == flockId)
                     && (from == null || u.Date >= from)
                     && (to == null || u.Date <= to))
            .OrderByBusinessChronologyDescending(u => u.Date)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

    public async Task AddAsync(FeedUsage entity, CancellationToken ct = default) =>
        await db.FeedUsages.AddAsync(entity, ct);
}
