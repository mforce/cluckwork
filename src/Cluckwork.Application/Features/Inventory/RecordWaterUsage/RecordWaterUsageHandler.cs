namespace Cluckwork.Application.Features.Inventory.RecordWaterUsage;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Flocks;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Inventory;

// Water consumed by a flock on a day (spec §12.5). No inventory behind it —
// a plain insert; corrections are Version-guarded updates.
public sealed class RecordWaterUsageHandler(
    IWaterUsageRepository waterUsages,
    IFlockRepository flocks,
    IFlockScopeGuard flockScope,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<Result<Guid>> HandleAsync(
        RecordWaterUsageCommand command, Guid accountId, CancellationToken ct)
    {
        // Spec §5.3 (#103): scoped workers may only record for assigned flocks.
        var scope = await flockScope.CheckAsync(command.FlockId, ct);
        if (scope.IsFailure) return Result.Failure<Guid>(scope.Error);

        // Tenant query filter scopes the lookup — foreign flocks read as null.
        var flock = await flocks.GetByIdAsync(command.FlockId, ct);
        if (flock is null)
            return Result.Failure<Guid>(Error.NotFound("Flock", command.FlockId));

        // Same lifecycle rule as production and feed.
        if (!flock.CanRecordProductionOn(command.Date))
            return Result.Failure<Guid>(Error.Validation(
                "WaterUsage.FlockNotActive",
                $"Flock '{flock.Name}' is {flock.Status.ToString().ToLowerInvariant()} — water cannot be recorded for this date."));

        if (command.Date > clock.TodayUtc)
            return Result.Failure<Guid>(Error.Validation(
                "WaterUsage.FutureDate", "Usage date cannot be in the future."));

        var quantity = command.Quantity ?? command.MeterEnd!.Value - command.MeterStart!.Value;
        var source = Enum.Parse<WaterSource>(command.Source, ignoreCase: true);

        var usage = WaterUsage.Create(
            Guid.NewGuid(), accountId, flock.Id, command.Date,
            quantity, command.Unit ?? "L", source,
            command.MeterStart, command.MeterEnd, clock.UtcNow, command.Note);

        await waterUsages.AddAsync(usage, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success(usage.Id);
    }
}
