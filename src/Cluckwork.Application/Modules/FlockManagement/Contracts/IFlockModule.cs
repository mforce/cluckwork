using Cluckwork.Domain.Common;
using Cluckwork.Domain.Modules.FlockManagement.Contracts;
using Cluckwork.Domain.Modules.FlockManagement.Flocks;

namespace Cluckwork.Application.Modules.FlockManagement.Contracts;

// #852: the Flock Management contract for adapters. Adapters reach Flock
// Management only through the types marked
// [ModuleContract("FlockManagement")]; peer modules use the narrower IFlockLookup
// and IMortalityLedger ports.
public interface IFlockModule
{
    // The EntityType Flock Management writes on a flock's audit rows; provenance reads key by it.
    const string FlockAuditEntityType = nameof(Flock);

    // The error code every lifecycle command returns for an unknown or invisible flock.
    const string FlockNotFoundCode = $"{nameof(Flock)}.NotFound";

    Task<Result<Guid>> CreateAsync(CreateFlockCommand command, Guid accountId, CancellationToken ct);

    Task<Result> UpdateAsync(UpdateFlockCommand command, CancellationToken ct);

    Task<Result> DepleteAsync(Guid id, CancellationToken ct);

    Task<Result> ArchiveAsync(Guid id, CancellationToken ct);

    Task<Result> ReactivateAsync(Guid id, CancellationToken ct);

    Task<Result<Guid>> RecordMovementAsync(RecordBirdMovementCommand command, Guid accountId, CancellationToken ct);

    Task<IReadOnlyList<FlockDetails>> SearchAsync(
        string? search, FlockEligibility eligibility, int limit, int offset, CancellationToken ct);

    // Net birds removed per flock; a flock with no movements is absent.
    Task<IReadOnlyDictionary<Guid, long>> GetBirdsRemovedAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    Task<long> GetBirdsRemovedAsync(Guid id, CancellationToken ct);

    // Newest first. Null when the flock is unknown or outside the caller's scope.
    Task<IReadOnlyList<BirdMovementDetails>?> ListMovementsAsync(
        Guid flockId, int limit, int offset, CancellationToken ct);
}

public sealed record BirdMovementDetails(
    Guid Id, Guid FlockId, DateOnly Date, BirdMovementType Type, int Quantity, string? Note);
