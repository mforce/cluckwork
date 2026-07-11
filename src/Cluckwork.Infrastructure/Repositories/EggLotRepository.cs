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
        Guid accountId, string gradeCode, DateOnly allocationDate,
        CancellationToken ct = default)
    {
        return await db.EggLots.FromSqlInterpolated($"""
            SELECT *
            FROM "EggLots"
            WHERE "AccountId" = {accountId}
              AND "GradeCode" = {gradeCode}
              AND "QuantityAvailable" > 0
              AND ("RestrictedUntil" IS NULL OR "RestrictedUntil" < {allocationDate})
            ORDER BY "ProductionDate"
            FOR UPDATE
            """)
            .IgnoreQueryFilters()
            .ToListAsync(ct);
    }

    public async Task AddAsync(EggLot entity, CancellationToken ct = default) =>
        await db.EggLots.AddAsync(entity, ct);

    public void Update(EggLot entity) => db.EggLots.Update(entity);

    public void Remove(EggLot entity) => db.EggLots.Remove(entity);
}
