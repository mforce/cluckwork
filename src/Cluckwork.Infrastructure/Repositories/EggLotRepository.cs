namespace Cluckwork.Infrastructure.Repositories;

using Cluckwork.Application.Features.EggLots;
using Cluckwork.Domain.Eggs;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed class EggLotRepository(AppDbContext db) : IEggLotRepository
{
    public Task<EggLot?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.EggLots.FirstOrDefaultAsync(e => e.Id == id, ct);

    public async Task<IReadOnlyList<EggLot>> ListAsync(
        Guid? eggGradeId, DateOnly? from, DateOnly? to,
        int limit, int offset, CancellationToken ct = default) =>
        await db.EggLots
            .AsNoTracking()
            .Where(l => eggGradeId == null || l.EggGradeId == eggGradeId)
            .Where(l => from == null || l.ProductionDate >= from)
            .Where(l => to == null || l.ProductionDate <= to)
            .OrderByDescending(l => l.ProductionDate).ThenByDescending(l => l.Id)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

    // Acquires a pessimistic FOR UPDATE lock for FIFO sale allocation (tech spec §3.3).
    // Provider-specific SQL is isolated here behind the repository port.
    // ONE statement for all grades, ordered (ProductionDate, Id): every locking
    // path (this and GetByIdsLockedAsync) acquires row locks in the same global
    // order, so a confirm and a void touching overlapping lots can never
    // deadlock. Per-grade statements in order-line order could.
    public async Task<IReadOnlyList<EggLot>> GetAvailableFifoLockedAsync(
        Guid accountId, IReadOnlyList<Guid> eggGradeIds, DateOnly allocationDate,
        CancellationToken ct = default)
    {
        if (eggGradeIds.Count == 0) return [];

        var scope = db.FlockScope;
        return await db.EggLots.FromSqlInterpolated($"""
            SELECT *
            FROM "EggLots"
            WHERE "AccountId" = {accountId}
              AND "EggGradeId" = ANY({eggGradeIds.ToArray()})
              AND "QuantityAvailable" > 0
              -- Same future-production guard the stock read applies (#35): without
              -- it a lot dated ahead of today is invisible in stock yet allocatable
              -- by a sale, so the two paths disagree about what exists. Such lots
              -- can be in the data already — the +1-day validator slack removed in
              -- this change is exactly what let an entry be dated tomorrow.
              AND "ProductionDate" <= {allocationDate}
              AND ("RestrictedUntil" IS NULL OR "RestrictedUntil" < {allocationDate})
              AND ({scope.IsUnrestricted} OR "FlockId" = ANY({scope.AssignedFlockIds.ToArray()}))
            ORDER BY "ProductionDate", "Id"
            FOR UPDATE
            """)
            .IgnoreQueryFilters()
            .ToListAsync(ct);
    }

    // FOR UPDATE lock on specific lots for the void-restore path (#60). Ordered
    // like the FIFO allocation query so void and confirm acquire overlapping
    // row locks in the same order (deadlock avoidance).
    public async Task<IReadOnlyList<EggLot>> GetByIdsLockedAsync(
        Guid accountId, IReadOnlyList<Guid> lotIds, CancellationToken ct = default)
    {
        if (lotIds.Count == 0) return [];

        var scope = db.FlockScope;
        return await db.EggLots.FromSqlInterpolated($"""
            SELECT *
            FROM "EggLots"
            WHERE "AccountId" = {accountId}
              AND "Id" = ANY({lotIds.ToArray()})
              AND ({scope.IsUnrestricted} OR "FlockId" = ANY({scope.AssignedFlockIds.ToArray()}))
            ORDER BY "ProductionDate", "Id"
            FOR UPDATE
            """)
            .IgnoreQueryFilters()
            .ToListAsync(ct);
    }

    // Entry adjust/void reconciliation (#69): every lot the entry's submit
    // generated, via the DailyEntryId link — (flock, date) would collide with
    // sibling entries from other houses on the same day. Emptied/restricted
    // lots included. Same canonical ordering as the other FOR UPDATE paths.
    public async Task<IReadOnlyList<EggLot>> GetByDailyEntryLockedAsync(
        Guid accountId, Guid dailyEntryId, CancellationToken ct = default)
    {
        var scope = db.FlockScope;
        return await db.EggLots.FromSqlInterpolated($"""
            SELECT *
            FROM "EggLots"
            WHERE "AccountId" = {accountId}
              AND "DailyEntryId" = {dailyEntryId}
              AND ({scope.IsUnrestricted} OR "FlockId" = ANY({scope.AssignedFlockIds.ToArray()}))
            ORDER BY "ProductionDate", "Id"
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
