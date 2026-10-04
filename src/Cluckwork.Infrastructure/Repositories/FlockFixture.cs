using Cluckwork.Application.Features.Flocks;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Flocks;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Repositories;

// Reads rely on the tenant and flock-scope query filters; AnyFlockAsync and
// the purges ignore them.
public sealed class FlockFixture(AppDbContext db) : IFlockFixture
{
    public Task<bool> BirdMovementExistsAsync(Guid flockId, DateOnly date, CancellationToken ct = default) =>
        db.BirdMovements.AnyAsync(m => m.FlockId == flockId && m.Date == date, ct);

    public Task<int> CountAdjustmentsAsync(Guid flockId, CancellationToken ct = default) =>
        db.BirdMovements.CountAsync(m => m.FlockId == flockId && m.Type == BirdMovementType.Adjustment, ct);

    public async Task<FlockFixtureCounts> CountAsync(CancellationToken ct = default) =>
        new(
            Flocks: await db.Flocks.CountAsync(ct),
            ActiveFlocks: await db.Flocks.CountAsync(f => f.Status == FlockStatus.Active, ct),
            DepletedFlocks: await db.Flocks.CountAsync(f => f.Status == FlockStatus.Depleted, ct),
            ArchivedFlocks: await db.Flocks.CountAsync(f => f.Status == FlockStatus.Archived, ct),
            BirdMovements: await db.BirdMovements.CountAsync(ct));

    public Task<bool> AnyFlockAsync(Guid accountId, CancellationToken ct = default) =>
        db.Flocks
            .IgnoreQueryFilters()
            .AnyAsync(f => f.AccountId == accountId, ct);

    public async Task<Result> DepleteAsync(Guid flockId, DateOnly asOf, CancellationToken ct = default)
    {
        var flock = await db.Flocks.FirstAsync(f => f.Id == flockId, ct);
        var result = flock.Deplete(asOf);
        if (result.IsSuccess) await db.SaveChangesAsync(ct);
        return result;
    }

    public Task PurgeBirdMovementsAsync(Guid accountId, CancellationToken ct = default) =>
        db.BirdMovements.IgnoreQueryFilters().Where(x => x.AccountId == accountId).ExecuteDeleteAsync(ct);

    public Task PurgeFlocksAsync(Guid accountId, CancellationToken ct = default) =>
        db.Flocks.IgnoreQueryFilters().Where(x => x.AccountId == accountId).ExecuteDeleteAsync(ct);
}
