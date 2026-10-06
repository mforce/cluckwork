using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.FlockManagement.Contracts;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Modules.Farm.Contracts;
using Cluckwork.Domain.Modules.FlockManagement.Flocks;

namespace Cluckwork.Application.Modules.FlockManagement.Flocks.CreateFlock;

public sealed class CreateFlockHandler(
    IFlockRepository flocks,
    IAuditWriter audit,
    IUnitOfWork unitOfWork)
{
    public async Task<Result<Guid>> HandleAsync(
        CreateFlockCommand command, Guid accountId, CancellationToken ct)
    {
        var flock = Flock.Create(
            Guid.NewGuid(), accountId,
            SeedDefaults.FarmId, SeedDefaults.HouseId,
            command.Name, command.Breed,
            command.PlacementDate, command.InitialCount);

        await flocks.AddAsync(flock, ct);
        await audit.WriteAsync(AuditActions.FlockCreate, nameof(Flock), flock.Id, ct: ct);
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success(flock.Id);
    }
}
