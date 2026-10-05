using Cluckwork.Application.Modules.FlockManagement.Contracts;
using Cluckwork.Application.Modules.FlockManagement.Flocks.ArchiveFlock;
using Cluckwork.Application.Modules.FlockManagement.Flocks.CreateFlock;
using Cluckwork.Application.Modules.FlockManagement.Flocks.DepleteFlock;
using Cluckwork.Application.Modules.FlockManagement.Flocks.ReactivateFlock;
using Cluckwork.Application.Modules.FlockManagement.Flocks.RecordBirdMovement;
using Cluckwork.Application.Modules.FlockManagement.Flocks.UpdateFlock;
using Cluckwork.Domain.Common;

namespace Cluckwork.Application.Modules.FlockManagement.Flocks;

public sealed class FlockModule(
    IFlockRepository flocks,
    IBirdMovementRepository movements,
    CreateFlockHandler createFlock,
    UpdateFlockHandler updateFlock,
    DepleteFlockHandler depleteFlock,
    ArchiveFlockHandler archiveFlock,
    ReactivateFlockHandler reactivateFlock,
    RecordBirdMovementHandler recordMovement) : IFlockModule
{
    public Task<Result<Guid>> CreateAsync(CreateFlockCommand command, Guid accountId, CancellationToken ct) =>
        createFlock.HandleAsync(command, accountId, ct);

    public Task<Result> UpdateAsync(UpdateFlockCommand command, CancellationToken ct) =>
        updateFlock.HandleAsync(command, ct);

    public Task<Result> DepleteAsync(Guid id, CancellationToken ct) => depleteFlock.HandleAsync(id, ct);

    public Task<Result> ArchiveAsync(Guid id, CancellationToken ct) => archiveFlock.HandleAsync(id, ct);

    public Task<Result> ReactivateAsync(Guid id, CancellationToken ct) => reactivateFlock.HandleAsync(id, ct);

    public Task<Result<Guid>> RecordMovementAsync(
        RecordBirdMovementCommand command, Guid accountId, CancellationToken ct) =>
        recordMovement.HandleAsync(command, accountId, ct);

    public async Task<IReadOnlyList<FlockDetails>> SearchAsync(
        string? search, FlockEligibility eligibility, int limit, int offset, CancellationToken ct) =>
        (await flocks.SearchAsync(search, eligibility, limit, offset, ct)).Select(FlockLookup.ToDetails).ToList();

    public async Task<IReadOnlyDictionary<Guid, long>> GetBirdsRemovedAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        await movements.RemovedForFlocksAsync(ids, ct);

    public Task<long> GetBirdsRemovedAsync(Guid id, CancellationToken ct) => movements.RemovedForFlockAsync(id, ct);

    public async Task<IReadOnlyList<BirdMovementDetails>?> ListMovementsAsync(
        Guid flockId, int limit, int offset, CancellationToken ct)
    {
        if (await flocks.GetByIdAsync(flockId, ct) is null) return null;
        var list = await movements.ListByFlockAsync(flockId, limit, offset, ct);
        return list.Select(m => new BirdMovementDetails(m.Id, m.FlockId, m.Date, m.Type, m.Quantity, m.Note)).ToList();
    }
}
