using Cluckwork.Domain.Modules.EggOperations.Eggs;

namespace Cluckwork.Application.Modules.EggOperations.Eggs;

public interface IEggInventoryMovementRepository
{
    Task AddAsync(EggInventoryMovement movement, CancellationToken ct = default);
    Task AddRangeAsync(IEnumerable<EggInventoryMovement> movements, CancellationToken ct = default);
    /// <summary>A lot's movements, newest first (CreatedAtUtc, then Sequence).</summary>
    Task<IReadOnlyList<EggInventoryMovement>> ListByLotAsync(Guid eggLotId, CancellationToken ct = default);
}
