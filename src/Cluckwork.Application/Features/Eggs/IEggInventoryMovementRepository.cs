namespace Cluckwork.Application.Features.Eggs;

using Cluckwork.Domain.Eggs;

public interface IEggInventoryMovementRepository
{
    Task AddAsync(EggInventoryMovement movement, CancellationToken ct = default);
    Task AddRangeAsync(IEnumerable<EggInventoryMovement> movements, CancellationToken ct = default);
    /// <summary>A lot's movements, newest first (CreatedAtUtc, then Id).</summary>
    Task<IReadOnlyList<EggInventoryMovement>> ListByLotAsync(Guid eggLotId, CancellationToken ct = default);
}
