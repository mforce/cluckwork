using Cluckwork.Domain.Inventory;

namespace Cluckwork.Application.Features.Inventory;

// Editable records (unlike feed usage): water has no lots/ledger behind it,
// so corrections are plain updates guarded by the Version token.
public interface IWaterUsageRepository
{
    Task<WaterUsage?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task AddAsync(WaterUsage entity, CancellationToken ct = default);

    // Newest first, optional flock/date filters, paged.
    Task<IReadOnlyList<WaterUsage>> ListAsync(
        Guid? flockId, DateOnly? from, DateOnly? to,
        int limit, int offset, CancellationToken ct = default);
}
