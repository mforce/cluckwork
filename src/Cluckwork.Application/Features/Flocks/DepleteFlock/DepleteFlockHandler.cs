namespace Cluckwork.Application.Features.Flocks.DepleteFlock;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Flocks;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Flocks;

public sealed class DepleteFlockHandler(
    IFlockRepository flocks,
    IUnitOfWork unitOfWork,
    IAuditWriter audit)
{
    public async Task<Result> HandleAsync(Guid flockId, CancellationToken ct)
    {
        var flock = await flocks.GetByIdAsync(flockId, ct);
        if (flock is null)
            return Result.Failure(Error.NotFound(nameof(Flock), flockId));

        // Operational date of the action (farm-local ≈ UTC for the MVP, #35).
        var result = flock.Deplete(DateOnly.FromDateTime(DateTime.UtcNow.Date));
        if (result.IsFailure)
            return result;

        flocks.Update(flock);
        // Same SaveChanges as the change (#93).
        await audit.WriteAsync("Flock.Deplete", "Flock", flock.Id,
            reason: null, details: null, ct: ct);

        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}
