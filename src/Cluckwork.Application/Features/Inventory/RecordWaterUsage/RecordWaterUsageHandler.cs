namespace Cluckwork.Application.Features.Inventory.RecordWaterUsage;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Flocks;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Inventory;
using Microsoft.Extensions.Logging;

// Water consumed by a flock on a day (spec §12.5). No inventory behind it —
// a plain insert; corrections are Version-guarded updates.
public sealed class RecordWaterUsageHandler(
    IWaterUsageRepository waterUsages,
    IFlockRepository flocks,
    IFlockScopeGuard flockScope,
    Cluckwork.Application.Features.DailyEntries.IDailyEntryRepository dailyEntries,
    IUnitOfWork unitOfWork,
    IClock clock,
    IFarmClock farmClock,
    ILogger<RecordWaterUsageHandler> logger)
{
    public async Task<Result<Guid>> HandleAsync(
        RecordWaterUsageCommand command, Guid accountId, CancellationToken ct)
    {
        // Spec §5.3 (#103): scoped workers may only record for assigned flocks.
        var scope = await flockScope.CheckAsync(command.FlockId, ct);
        if (scope.IsFailure) return Result.Failure<Guid>(scope.Error).LogFailure(logger, "RecordWaterUsage");

        // Tenant query filter scopes the lookup — foreign flocks read as null.
        var flock = await flocks.GetByIdAsync(command.FlockId, ct);
        if (flock is null)
            return Result.Failure<Guid>(Error.NotFound("Flock", command.FlockId)).LogFailure(logger, "RecordWaterUsage");

        // Same lifecycle rule as production and feed.
        if (!flock.CanRecordProductionOn(command.Date))
            return Result.Failure<Guid>(Error.Validation(
                "WaterUsage.FlockNotActive",
                $"Flock '{flock.Name}' is {flock.Status.ToString().ToLowerInvariant()} — water cannot be recorded for this date.")).LogFailure(logger, "RecordWaterUsage");

        if (command.Date > await farmClock.TodayAsync(ct))
            return Result.Failure<Guid>(Error.Validation(
                "WaterUsage.FutureDate", "Usage date cannot be in the future.")).LogFailure(logger, "RecordWaterUsage");

        var quantity = command.Quantity ?? command.MeterEnd!.Value - command.MeterStart!.Value;
        var source = Enum.Parse<WaterSource>(command.Source, ignoreCase: true);

        // #446 — record-time stamp: the non-voided entry that exists for this
        // flock's own (farm, house, flock, date) right now, or null. Never
        // backfilled; Update never touches it. Same contract as feed — see
        // RecordFeedUsageHandler's comment for the full reasoning.
        var dailyEntryId = (await dailyEntries.FindByNaturalKeyAsync(
            accountId, flock.FarmId, flock.HouseId, command.FlockId, command.Date, ct))?.Id;

        var usage = WaterUsage.Create(
            Guid.NewGuid(), accountId, flock.Id, command.Date,
            quantity, command.Unit ?? "L", source,
            command.MeterStart, command.MeterEnd, clock.UtcNow, command.Note,
            dailyEntryId);

        await waterUsages.AddAsync(usage, ct);
        await unitOfWork.SaveChangesAsync(ct);
        logger.LogInformation(
            "Water usage {WaterUsageId} recorded: {Quantity} {Unit} for flock {FlockId} on {UsageDate}",
            usage.Id, quantity, command.Unit ?? "L", command.FlockId, command.Date);
        return Result.Success(usage.Id);
    }
}
