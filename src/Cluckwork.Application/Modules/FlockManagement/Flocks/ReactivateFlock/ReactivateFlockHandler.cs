using Cluckwork.Application.Common;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Modules.FlockManagement.Flocks;

namespace Cluckwork.Application.Modules.FlockManagement.Flocks.ReactivateFlock;

public sealed class ReactivateFlockHandler(
    IFlockRepository flocks,
    IUnitOfWork unitOfWork,
    IAuditWriter audit)
{
    public async Task<Result> HandleAsync(Guid flockId, CancellationToken ct)
    {
        var flock = await flocks.GetByIdAsync(flockId, ct);
        if (flock is null)
            return Result.Failure(Error.NotFound(nameof(Flock), flockId));

        var result = flock.Reactivate();
        if (result.IsFailure)
            return result;

        flocks.Update(flock);
        // Same SaveChanges as the change (#93).
        await audit.WriteAsync(AuditActions.FlockReactivate, "Flock", flock.Id,
            reason: null, details: null, ct: ct);

        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}
