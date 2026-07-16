namespace Cluckwork.Infrastructure.Repositories;

using Cluckwork.Application.Features.EggLots;
using Cluckwork.Domain.Eggs;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed class EggLotRepository(AppDbContext db) : IEggLotRepository
{
    public Task<EggLot?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.EggLots.FirstOrDefaultAsync(e => e.Id == id, ct);

    // Acquires a pessimistic FOR UPDATE lock for FIFO sale allocation (tech spec §3.3).
    // Provider-specific SQL is isolated here behind the repository port.
    public async Task<IReadOnlyList<EggLot>> GetAvailableFifoLockedAsync(
        Guid accountId, Guid eggGradeId, DateOnly allocationDate,
        CancellationToken ct = default)
    {
        return await db.EggLots.FromSqlInterpolated($"""
            SELECT *
            FROM "EggLots"
            WHERE "AccountId" = {accountId}
              AND "EggGradeId" = {eggGradeId}
              AND "QuantityAvailable" > 0
              AND ("RestrictedUntil" IS NULL OR "RestrictedUntil" < {allocationDate})
            ORDER BY "ProductionDate"
            FOR UPDATE
            """)
            .IgnoreQueryFilters()
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<StockByGrade>> GetStockByGradeAsync(
        DateOnly asOfDate, CancellationToken ct = default)
    {
        // Aggregate in SQL by grade id, then attach grade names in memory — the
        // grade set is tiny and the join-into-GroupBy shape doesn't translate.
        var sums = await db.EggLots
            .AsNoTracking()
            // Future-dated production (if it ever slips in) is not current stock.
            .Where(l => l.ProductionDate <= asOfDate)
            .GroupBy(l => l.EggGradeId)
            .Select(g => new
            {
                EggGradeId = g.Key,
                Available = g.Sum(l =>
                    l.RestrictedUntil == null || l.RestrictedUntil < asOfDate ? l.QuantityAvailable : 0),
                Restricted = g.Sum(l =>
                    l.RestrictedUntil != null && l.RestrictedUntil >= asOfDate ? l.QuantityAvailable : 0),
            })
            .ToListAsync(ct);

        if (sums.Count == 0) return [];

        var ids = sums.Select(s => s.EggGradeId).ToList();
        var grades = await db.EggGrades
            .AsNoTracking()
            .Where(g => ids.Contains(g.Id))
            .ToDictionaryAsync(g => g.Id, ct);

        return sums
            .Select(s =>
            {
                var grade = grades.GetValueOrDefault(s.EggGradeId);
                return new StockByGrade(
                    s.EggGradeId,
                    grade?.Name ?? s.EggGradeId.ToString(),
                    grade?.SortOrder ?? int.MaxValue,
                    s.Available, s.Restricted);
            })
            .OrderBy(r => r.SortOrder).ThenBy(r => r.GradeName)
            .ToList();
    }

    public async Task AddAsync(EggLot entity, CancellationToken ct = default) =>
        await db.EggLots.AddAsync(entity, ct);

    public void Update(EggLot entity) => db.EggLots.Update(entity);

    public void Remove(EggLot entity) => db.EggLots.Remove(entity);
}
