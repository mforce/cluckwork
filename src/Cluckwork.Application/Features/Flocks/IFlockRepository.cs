namespace Cluckwork.Application.Features.Flocks;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Flocks;

public interface IFlockRepository : IRepository<Flock, Guid>
{
    // Archived flocks are hidden by default — they only appear in the
    // management view (includeArchived: true). Depleted flocks stay visible.
    Task<IReadOnlyList<Flock>> ListAsync(
        int limit, int offset, bool includeArchived = false, CancellationToken ct = default);
}
