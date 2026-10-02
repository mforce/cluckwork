using Cluckwork.Domain.Flocks;

namespace Cluckwork.Application.Features.Flocks;

// #852: Flock Management's read port for peer modules and adapters. Every read
// keeps the tenant and flock-scope query filters (#613) except
// GetForFlockScopedWriteAsync, which reinstates AccountId itself (#388).
public interface IFlockLookup
{
    Task<FlockDetails?> GetAsync(Guid id, CancellationToken ct);

    // For a write that has already passed IFlockScopeGuard; see
    // IFlockRepository.GetByIdForFlockScopedWriteAsync. Takes no lock and tracks
    // nothing, so a second call sees a change committed after the first (#1022).
    Task<FlockDetails?> GetForFlockScopedWriteAsync(Guid id, Guid accountId, CancellationToken ct);

    // See IFlockRepository.GetDisplayNamesAsync: a missing key is not "unnamed".
    Task<IReadOnlyDictionary<Guid, FlockReference>> GetDisplayNamesAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct);

    // Exact name match across every status. Duplicate names are legal, so a
    // caller must handle Ambiguous rather than take the first match.
    Task<FlockNameResolution> ResolveByNameAsync(string name, CancellationToken ct);
}

public sealed record FlockDetails(
    Guid Id,
    Guid FarmId,
    Guid HouseId,
    string Name,
    string Breed,
    DateOnly PlacementDate,
    int InitialCount,
    FlockStatus Status,
    DateOnly? DepletedOn,
    DateOnly? ArchivedOn,
    int Version)
{
    public bool CanRecordProductionOn(DateOnly date) => Flock.CanRecordProductionOn(Status, DepletedOn, date);
}

public abstract record FlockNameResolution
{
    private FlockNameResolution() { }

    public sealed record Found(FlockReference Flock) : FlockNameResolution;

    public sealed record NotFound : FlockNameResolution;

    // Ordered by id.
    public sealed record Ambiguous(IReadOnlyList<FlockReference> Candidates) : FlockNameResolution;
}
