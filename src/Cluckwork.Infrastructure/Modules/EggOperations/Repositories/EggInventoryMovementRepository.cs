using Cluckwork.Application.Modules.EggOperations.Eggs;
using Cluckwork.Domain.Modules.EggOperations.Eggs;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Modules.EggOperations.Repositories;

public sealed class EggInventoryMovementRepository(AppDbContext db) : IEggInventoryMovementRepository
{
    public async Task AddAsync(EggInventoryMovement movement, CancellationToken ct = default) =>
        await db.EggInventoryMovements.AddAsync(movement, ct);

    public async Task AddRangeAsync(IEnumerable<EggInventoryMovement> movements, CancellationToken ct = default) =>
        await db.EggInventoryMovements.AddRangeAsync(movements, ct);

    public async Task<IReadOnlyList<EggInventoryMovement>> ListByLotAsync(
        Guid eggLotId, CancellationToken ct = default) =>
        await ByLot(eggLotId).ToListAsync(ct);

    public async Task<IReadOnlyList<EggInventoryMovement>> ListByLotAsync(
        Guid eggLotId, int limit, int offset, CancellationToken ct = default) =>
        await ByLot(eggLotId).Skip(offset).Take(limit).ToListAsync(ct);

    private IQueryable<EggInventoryMovement> ByLot(Guid eggLotId) =>
        db.EggInventoryMovements
            .AsNoTracking()
            .Where(m => m.EggLotId == eggLotId)
            .OrderByCreationChronologyDescending();
}
