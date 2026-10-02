using Cluckwork.Domain.Flocks;

namespace Cluckwork.Application.Features.Flocks;

public sealed class FlockLookup(IFlockRepository flocks) : IFlockLookup
{
    public async Task<FlockDetails?> GetAsync(Guid id, CancellationToken ct) =>
        await flocks.GetReadOnlyAsync(id, ct) is { } flock ? ToDetails(flock) : null;

    public async Task<FlockDetails?> GetForFlockScopedWriteAsync(Guid id, Guid accountId, CancellationToken ct) =>
        await flocks.GetReadOnlyForFlockScopedWriteAsync(id, accountId, ct) is { } flock ? ToDetails(flock) : null;

    public Task<IReadOnlyDictionary<Guid, FlockReference>> GetDisplayNamesAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        flocks.GetDisplayNamesAsync(ids, ct);

    public async Task<FlockNameResolution> ResolveByNameAsync(string name, CancellationToken ct) =>
        await flocks.ListByNameAsync(name, ct) switch
        {
            [] => new FlockNameResolution.NotFound(),
            [var only] => new FlockNameResolution.Found(only),
            var many => new FlockNameResolution.Ambiguous(many),
        };

    internal static FlockDetails ToDetails(Flock f) =>
        new(f.Id, f.FarmId, f.HouseId, f.Name, f.Breed, f.PlacementDate, f.InitialCount,
            f.Status, f.DepletedOn, f.ArchivedOn, f.Version);
}
