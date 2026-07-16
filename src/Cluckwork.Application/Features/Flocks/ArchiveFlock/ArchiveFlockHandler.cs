namespace Cluckwork.Application.Features.Flocks.ArchiveFlock;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Flocks;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Flocks;

public sealed class ArchiveFlockHandler(
    IFlockRepository flocks,
    IUnitOfWork unitOfWork)
{
    public async Task<Result> HandleAsync(Guid flockId, CancellationToken ct)
    {
        var flock = await flocks.GetByIdAsync(flockId, ct);
        if (flock is null)
            return Result.Failure(Error.NotFound(nameof(Flock), flockId));

        // Operational date of the action (farm-local ≈ UTC for the MVP, #35).
        var result = flock.Archive(DateOnly.FromDateTime(DateTime.UtcNow.Date));
        if (result.IsFailure)
            return result;

        flocks.Update(flock);
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}
