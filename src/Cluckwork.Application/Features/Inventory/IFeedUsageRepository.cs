namespace Cluckwork.Application.Features.Inventory;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Inventory;

// Create-only like the movement ledger: corrections happen through
// compensating inventory adjustments, never by editing the usage record.
public interface IFeedUsageRepository : IRepository<FeedUsage, Guid>
{
    // Newest first, optional flock/date filters, paged.
    Task<IReadOnlyList<FeedUsage>> ListAsync(
        Guid? flockId, DateOnly? from, DateOnly? to,
        int limit, int offset, CancellationToken ct = default);
}
