using Cluckwork.Domain.Eggs;

namespace Cluckwork.Application.Features.Eggs;

public interface IEggInventoryMovementRepository
{
    Task AddAsync(EggInventoryMovement movement, CancellationToken ct = default);
    Task AddRangeAsync(IEnumerable<EggInventoryMovement> movements, CancellationToken ct = default);
    /// <summary>A lot's movements, newest first (CreatedAtUtc, then Sequence).</summary>
    Task<IReadOnlyList<EggInventoryMovement>> ListByLotAsync(Guid eggLotId, CancellationToken ct = default);
}
