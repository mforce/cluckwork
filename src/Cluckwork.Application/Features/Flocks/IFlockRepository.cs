namespace Cluckwork.Application.Features.Flocks;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Flocks;

public interface IFlockRepository : IRepository<Flock, Guid>
{
    // Archived flocks are hidden by default — they only appear in the
    // management view (includeArchived: true). Depleted flocks stay visible.
    Task<IReadOnlyList<Flock>> ListAsync(
        int limit, int offset, bool includeArchived = false, CancellationToken ct = default);

    // Write-side lifecycle lookup (#388). Bypasses the request-start flock
    // snapshot after the live FlockScopeGuard succeeds, but reinstates AccountId
    // explicitly. This closes the assignment-change race without exposing flock
    // state to an unassigned caller or crossing tenants.
    Task<Flock?> GetByIdForFlockScopedWriteAsync(
        Guid id, Guid accountId, CancellationToken ct = default);
}
