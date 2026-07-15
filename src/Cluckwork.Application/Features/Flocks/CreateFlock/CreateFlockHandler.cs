namespace Cluckwork.Application.Features.Flocks.CreateFlock;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Flocks;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Flocks;

public sealed class CreateFlockHandler(
    IFlockRepository flocks,
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
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success(flock.Id);
    }
}
