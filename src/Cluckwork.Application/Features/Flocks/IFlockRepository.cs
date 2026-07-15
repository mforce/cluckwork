namespace Cluckwork.Application.Features.Flocks;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Flocks;

public interface IFlockRepository : IRepository<Flock, Guid>
{
    Task<IReadOnlyList<Flock>> ListAsync(CancellationToken ct = default);
}
