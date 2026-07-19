namespace Cluckwork.Application.Features.Flocks.UpdateFlock;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Flocks;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Flocks;

public sealed class UpdateFlockHandler(
    IFlockRepository flocks,
    IUnitOfWork unitOfWork,
    IAuditWriter audit)
{
    public async Task<Result> HandleAsync(UpdateFlockCommand command, CancellationToken ct)
    {
        var flock = await flocks.GetByIdAsync(command.FlockId, ct);
        if (flock is null)
            return Result.Failure(Error.NotFound(nameof(Flock), command.FlockId));

        var result = flock.Update(
            command.Name, command.Breed, command.PlacementDate, command.InitialCount);
        if (result.IsFailure)
            return result;

        flocks.Update(flock);
        // Same SaveChanges as the change (#93).
        await audit.WriteAsync("Flock.Update", "Flock", flock.Id,
            reason: null, details: null, ct: ct);

        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}
