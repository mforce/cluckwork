namespace Cluckwork.Application.Features.Flocks.DepleteFlock;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Flocks;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Flocks;

public sealed class DepleteFlockHandler(
    IFlockRepository flocks,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    IFarmClock farmClock)
{
    public async Task<Result> HandleAsync(Guid flockId, CancellationToken ct)
    {
        var flock = await flocks.GetByIdAsync(flockId, ct);
        if (flock is null)
            return Result.Failure(Error.NotFound(nameof(Flock), flockId));

        // Operational date of the action, on the farm's own calendar (#35) —
        // this date is STORED, so a UTC one persists the wrong day and then
        // silently decides which backfill entries the flock still accepts.
        var result = flock.Deplete(await farmClock.TodayAsync(ct));
        if (result.IsFailure)
            return result;

        flocks.Update(flock);
        // Same SaveChanges as the change (#93).
        await audit.WriteAsync(AuditActions.FlockDeplete, "Flock", flock.Id,
            reason: null, details: null, ct: ct);

        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}
