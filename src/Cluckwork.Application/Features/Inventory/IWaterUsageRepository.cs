namespace Cluckwork.Application.Features.Inventory;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Inventory;

// Editable records (unlike feed usage): water has no lots/ledger behind it,
// so corrections are plain updates guarded by the Version token.
public interface IWaterUsageRepository : IRepository<WaterUsage, Guid>
{
    // Newest first, optional flock/date filters, paged.
    Task<IReadOnlyList<WaterUsage>> ListAsync(
        Guid? flockId, DateOnly? from, DateOnly? to,
        int limit, int offset, CancellationToken ct = default);
}
