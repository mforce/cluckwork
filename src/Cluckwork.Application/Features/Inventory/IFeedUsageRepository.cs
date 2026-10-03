using Cluckwork.Domain.Inventory;

namespace Cluckwork.Application.Features.Inventory;

// Create-only like the movement ledger: corrections happen through
// compensating inventory adjustments, never by editing the usage record.
public interface IFeedUsageRepository
{
    Task AddAsync(FeedUsage entity, CancellationToken ct = default);

    // Newest first, optional flock/date filters, paged.
    Task<IReadOnlyList<FeedUsage>> ListAsync(
        Guid? flockId, DateOnly? from, DateOnly? to,
        int limit, int offset, CancellationToken ct = default);
}
