namespace Cluckwork.Application.Features.Flocks.RecordBirdMovement;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Flocks;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Flocks;
using Microsoft.Extensions.Logging;

public sealed class RecordBirdMovementHandler(
    IFlockRepository flocks,
    IBirdMovementRepository movements,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    ILogger<RecordBirdMovementHandler> logger)
{
    public async Task<Result<Guid>> HandleAsync(
        RecordBirdMovementCommand command, Guid accountId, CancellationToken ct)
    {
        var flock = await flocks.GetByIdAsync(command.FlockId, ct);
        if (flock is null)
            return Result.Failure<Guid>(Error.NotFound(nameof(Flock), command.FlockId)).LogFailure(logger, "RecordBirdMovement");

        // Same lifecycle gate as daily entries (#47): depleted flocks accept
        // backfill dated on/before depletion; archived flocks accept nothing.
        if (!flock.CanRecordProductionOn(command.Date))
            return Result.Failure<Guid>(Error.Validation(
                "BirdMovement.FlockNotActive",
                $"Flock '{flock.Name}' is {flock.Status.ToString().ToLowerInvariant()} — movements cannot be recorded for {command.Date:yyyy-MM-dd}.")).LogFailure(logger, "RecordBirdMovement");

        var type = Enum.Parse<BirdMovementType>(command.Type, ignoreCase: true);
        var movement = BirdMovement.Create(
            Guid.NewGuid(), accountId, flock.Id,
            command.Date, type, command.Quantity, command.Note);

        await movements.AddAsync(movement, ct);
        // Same SaveChanges as the change (#93).
        await audit.WriteAsync(AuditActions.FlockBirdMovement, "Flock", flock.Id,
            reason: command.Note, details: new { command.Type, command.Quantity }, ct: ct);

        await unitOfWork.SaveChangesAsync(ct);
        logger.LogInformation(
            "Bird movement {BirdMovementId} recorded for flock {FlockId} on {MovementDate}: {MovementType} x{Quantity}",
            movement.Id, flock.Id, command.Date, command.Type, command.Quantity);
        return Result.Success(movement.Id);
    }
}
