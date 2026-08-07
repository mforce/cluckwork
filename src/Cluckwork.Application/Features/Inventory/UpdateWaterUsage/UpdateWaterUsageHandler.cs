namespace Cluckwork.Application.Features.Inventory.UpdateWaterUsage;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Flocks;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Inventory;

// Correcting a water record (#67; corrections join the admin-gated surface
// with #73). Optimistic concurrency is end-to-end: the client sends the base
// Version it loaded, a mismatch is a deterministic 409 — the EF token alone
// only guarded this handler's own read→save window, not the user's
// read→edit→save cycle (codex review of PR #76).
public sealed class UpdateWaterUsageHandler(
    IWaterUsageRepository waterUsages,
    IFlockRepository flocks,
    IUnitOfWork unitOfWork,
    IAuditWriter audit)
{
    public async Task<Result> HandleAsync(
        UpdateWaterUsageCommand command, CancellationToken ct)
    {
        // Tenant query filter scopes the lookup.
        var usage = await waterUsages.GetByIdAsync(command.WaterUsageId, ct);
        if (usage is null)
            return Result.Failure(Error.NotFound(nameof(WaterUsage), command.WaterUsageId));

        if (usage.Version != command.Version)
            return Result.Failure(Error.Conflict(
                "WaterUsage.VersionMismatch",
                "This record was changed since you loaded it — reload and retry."));

        // Same lifecycle rule as recording: an archived flock's history is
        // read-only; depleted allows corrections within the backfill window.
        var flock = await flocks.GetByIdAsync(usage.FlockId, ct);
        if (flock is not null && !flock.CanRecordProductionOn(usage.Date))
            return Result.Failure(Error.Validation(
                "WaterUsage.FlockNotActive",
                $"Flock '{flock.Name}' is {flock.Status.ToString().ToLowerInvariant()} — this record can no longer be corrected."));

        var quantity = command.Quantity ?? command.MeterEnd!.Value - command.MeterStart!.Value;
        var source = Enum.Parse<WaterSource>(command.Source, ignoreCase: true);

        var result = usage.Update(
            quantity, command.Unit ?? usage.Unit, source,
            command.MeterStart, command.MeterEnd, command.Note);
        if (result.IsFailure)
            return result;

        // Same SaveChanges as the change (#93).
        await audit.WriteAsync(AuditActions.WaterUsageCorrect, nameof(WaterUsage), usage.Id,
            reason: null, new { usage.Quantity, usage.Unit }, ct);

        // The EF Version token still backstops the microsecond window between
        // the check above and this save.
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}
