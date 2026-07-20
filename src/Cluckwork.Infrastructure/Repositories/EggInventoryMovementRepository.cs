namespace Cluckwork.Infrastructure.Repositories;

using Cluckwork.Application.Features.Eggs;
using Cluckwork.Domain.Eggs;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed class EggInventoryMovementRepository(AppDbContext db) : IEggInventoryMovementRepository
{
    public async Task AddAsync(EggInventoryMovement movement, CancellationToken ct = default) =>
        await db.EggInventoryMovements.AddAsync(movement, ct);

    public async Task AddRangeAsync(IEnumerable<EggInventoryMovement> movements, CancellationToken ct = default) =>
        await db.EggInventoryMovements.AddRangeAsync(movements, ct);

    public async Task<IReadOnlyList<EggInventoryMovement>> ListByLotAsync(
        Guid eggLotId, CancellationToken ct = default) =>
        await db.EggInventoryMovements
            .AsNoTracking()
            .Where(m => m.EggLotId == eggLotId)
            .OrderByDescending(m => m.CreatedAtUtc).ThenByDescending(m => m.Id)
            .ToListAsync(ct);
}
