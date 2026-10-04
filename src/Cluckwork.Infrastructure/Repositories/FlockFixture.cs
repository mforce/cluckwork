using Cluckwork.Application.Features.Flocks;
using Cluckwork.Domain.Flocks;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Repositories;

// Reads rely on the tenant and flock-scope query filters.
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
}
