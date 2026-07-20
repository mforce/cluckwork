namespace Cluckwork.Application.Common;

using Cluckwork.Domain.Common;

// #103 (spec §5.3): workers may record production only for assigned flocks.
// Owners/Managers always pass. A worker with NO assignment rows keeps
// account-wide access (grandfathering #73 workers); the first assignment
// narrows them. Farm/house-wide assignments (FlockId null) grant everything
// in the single-farm MVP.
public interface IFlockScopeGuard
{
    Task<Result> CheckAsync(Guid flockId, CancellationToken ct = default);
}
