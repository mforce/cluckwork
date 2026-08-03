namespace Cluckwork.Application.Features.DailyEntries.AdjustDailyEntry;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.DailyEntries;
using Cluckwork.Application.Features.EggGrades;
using Cluckwork.Application.Features.EggLots;
using Cluckwork.Application.Features.Eggs;
using Cluckwork.Application.Features.Flocks;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Eggs;
using Cluckwork.Domain.Flocks;
using Microsoft.Extensions.Logging;

// #69 — the corrective half of the production → stock bridge. Adjusting a
// submitted/locked entry must keep three things consistent in ONE transaction:
// the entry itself (totals + grade lines, ManagerAdjusted), its egg lots
// (grown/shrunk/added/emptied to match the new grade lines — but never below
// what a lot has already sold), and the bird ledger (a compensating movement
// for any mortality change; the original rows are never edited).
public sealed class AdjustDailyEntryHandler(
    IDailyEntryRepository entries,
    IEggLotRepository eggLots,
    IEggGradeRepository eggGrades,
    IBirdMovementRepository birdMovements,
    IFlockRepository flocks,
    IEggInventoryMovementRepository eggMovements,
    IClock clock,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    ILogger<AdjustDailyEntryHandler> logger)
{
    public async Task<Result<AdjustDailyEntryResponse>> HandleAsync(
        AdjustDailyEntryCommand command, Guid accountId, CancellationToken ct)
    {
        var entry = await entries.GetByIdAsync(command.DailyEntryId, ct);
        if (entry is null)
            return Result.Failure<AdjustDailyEntryResponse>(
                Error.NotFound(nameof(DailyEntry), command.DailyEntryId)).LogFailure(logger, "AdjustDailyEntry");

        // End-to-end optimistic concurrency (PR #77 contract): the client's
        // base version must match; the EF token backstops the save itself.
        if (entry.Version != command.Version)
            return Result.Failure<AdjustDailyEntryResponse>(Error.Conflict(
                "DailyEntry.VersionMismatch",
                "The entry was changed by someone else. Reload it and re-apply the adjustment."))
                .LogFailure(logger, "AdjustDailyEntry");

        // Archived flocks are read-only history — same gate as recording.
        // Depleted flocks accept corrections for dates up to their depletion.
        var flock = await flocks.GetByIdAsync(entry.FlockId, ct);
        if (flock is null)
            return Result.Failure<AdjustDailyEntryResponse>(
                Error.NotFound(nameof(Flock), entry.FlockId)).LogFailure(logger, "AdjustDailyEntry");
        if (!flock.CanRecordProductionOn(entry.Date))
            return Result.Failure<AdjustDailyEntryResponse>(Error.Validation(
                "DailyEntry.FlockNotActive",
                $"Flock '{flock.Name}' is {flock.Status.ToString().ToLowerInvariant()} — this entry can no longer be adjusted."))
                .LogFailure(logger, "AdjustDailyEntry");

        // Grade ids must be the tenant's own for this farm. Active + saleable
        // for NEW lines; ids already on the entry are grandfathered so a line
        // referencing a since-deactivated grade can still be kept or corrected
        // (the deactivated-grade lesson from PR #74).
        if (command.Grades is { Count: > 0 })
        {
            var allowed = (await eggGrades.ListActiveAsync(entry.FarmId, ct))
                .Where(g => g.IsSaleable)
                .Select(g => g.Id)
                .Concat(entry.Grades.Select(l => l.EggGradeId))
                .ToHashSet();

            if (command.Grades.Any(g => !allowed.Contains(g.EggGradeId)))
                return Result.Failure<AdjustDailyEntryResponse>(Error.Validation(
                    "DailyEntry.UnknownGrade",
                    "One or more egg grades do not exist, are inactive, or are not saleable."))
                    .LogFailure(logger, "AdjustDailyEntry");

            // #396 — deliberately NOT folded into `allowed` above: that set
            // unions the lines already on the entry, so a condition grade could
            // be talked past it. ConditionGradeGuard asks the catalog directly.
            var conditionGrade = await ConditionGradeGuard.CheckAsync(
                eggGrades, command.Grades.Select(g => g.EggGradeId), ct);
            if (conditionGrade is not null)
                return Result.Failure<AdjustDailyEntryResponse>(conditionGrade)
                    .LogFailure(logger, "AdjustDailyEntry");
        }

        var previousMortality = entry.MortalityCount;
        var hadGradeLines = entry.Grades.Count > 0;
        var grades = command.Grades?
            .Select(g => new GradeQuantity(g.EggGradeId, g.Quantity))
            .ToList();

        Result<AdjustDailyEntryResponse>? failure = null;

        await unitOfWork.ExecuteInTransactionAsync(async transactionCt =>
        {
            // Lock EVERY lot this entry's submit generated before touching
            // anything — canonical (ProductionDate, Id) order shared with
            // confirm/void, so an adjust racing a sale allocation can't
            // deadlock; the sold quantities we validate against can't move.
            var lockedLots = await eggLots.GetByDailyEntryLockedAsync(
                accountId, entry.Id, transactionCt);

            // Grade lines but no linked lots = the entry predates the
            // lot-to-entry linkage and couldn't be backfilled unambiguously;
            // creating fresh lots here would double its stock.
            if (hadGradeLines && lockedLots.Count == 0)
            {
                failure = Result.Failure<AdjustDailyEntryResponse>(Error.Domain(
                    "DailyEntry.PredatesLotTracking",
                    "This entry predates lot-to-entry tracking and cannot be adjusted."));
                return false;
            }

            var adjust = entry.ManagerAdjust(
                command.TotalEggs, command.CrackedEggs, command.DirtyEggs,
                command.DiscardedEggs, command.MortalityCount, command.Reason, grades);
            if (adjust.IsFailure)
            {
                failure = Result.Failure<AdjustDailyEntryResponse>(adjust.Error);
                return false;
            }

            // Reconcile lots against the entry's (post-adjust) grade lines.
            // One lot per grade is the submit invariant; if duplicates ever
            // exist, every lot first keeps its sold floor (sold eggs are
            // untouchable wherever they sit) and the first lot in canonical
            // order carries the remainder.
            var targets = entry.Grades.ToDictionary(l => l.EggGradeId, l => l.Quantity);
            foreach (var gradeLots in lockedLots.GroupBy(l => l.EggGradeId))
            {
                var target = targets.GetValueOrDefault(gradeLots.Key, 0);
                var lots = gradeLots.ToList();
                var totalSold = lots.Sum(l => l.QuantityProduced - l.QuantityAvailable);
                if (target < totalSold)
                {
                    failure = Result.Failure<AdjustDailyEntryResponse>(await NameGradeAsync(
                        Error.Domain(
                            "EggLot.SoldExceedsAdjusted",
                            $"{totalSold} eggs of this grade are already sold or allocated; production cannot be set below that."),
                        gradeLots.Key, transactionCt));
                    return false;
                }

                var remainder = target - totalSold;
                for (var i = 0; i < lots.Count; i++)
                {
                    var sold = lots[i].QuantityProduced - lots[i].QuantityAvailable;
                    var newQuantity = sold + (i == 0 ? remainder : 0);
                    if (lots[i].QuantityProduced == newQuantity) continue;

                    var availableBefore = lots[i].QuantityAvailable;
                    var result = lots[i].AdjustProduction(newQuantity);
                    if (result.IsFailure)
                    {
                        // Unreachable given the sold floor above; backstop.
                        failure = Result.Failure<AdjustDailyEntryResponse>(
                            await NameGradeAsync(result.Error, gradeLots.Key, transactionCt));
                        return false;
                    }

                    // Ledger row (#101): the reconciliation delta, explicit and
                    // in-transaction.
                    var availableDelta = lots[i].QuantityAvailable - availableBefore;
                    if (availableDelta != 0)
                        await eggMovements.AddAsync(EggInventoryMovement.Create(
                            Guid.NewGuid(), accountId, lots[i].Id, EggMovementType.Adjustment,
                            availableDelta, nameof(DailyEntry), entry.Id, clock.UtcNow,
                            reason: command.Reason), transactionCt);
                }
            }

            // Grade lines the entry gained that never had a lot (grade added
            // by the adjustment) get one now, dated and linked like the rest.
            foreach (var (gradeId, quantity) in targets)
            {
                if (lockedLots.Any(l => l.EggGradeId == gradeId)) continue;
                var newLot = EggLot.Create(
                    Guid.NewGuid(), accountId, entry.FlockId, entry.Date, gradeId, quantity,
                    dailyEntryId: entry.Id);
                await eggLots.AddAsync(newLot, transactionCt);
                // Ledger row (#101): a lot born from an adjustment opens with an
                // Adjustment movement, not Production — the entry was already
                // submitted; this quantity is a correction.
                if (quantity > 0)
                    await eggMovements.AddAsync(EggInventoryMovement.Create(
                        Guid.NewGuid(), accountId, newLot.Id, EggMovementType.Adjustment,
                        quantity, nameof(DailyEntry), entry.Id, clock.UtcNow,
                        reason: command.Reason), transactionCt);
            }

            // Mortality delta lands as a NEW ledger row tied to the entry —
            // the original submit-generated row is never edited. More deaths →
            // Mortality; fewer → a negative Adjustment (adds birds back).
            var delta = entry.MortalityCount - previousMortality;
            if (delta != 0)
            {
                await birdMovements.AddAsync(BirdMovement.Create(
                    Guid.NewGuid(), accountId, entry.FlockId, entry.Date,
                    delta > 0 ? BirdMovementType.Mortality : BirdMovementType.Adjustment,
                    delta,
                    note: MovementNote("Entry adjusted: ", entry.AdjustReason),
                    dailyEntryId: entry.Id), transactionCt);
            }

            // Same transaction as the change (#93): rolls back with it.
            await audit.WriteAsync("DailyEntry.Adjust", nameof(DailyEntry), entry.Id,
                command.Reason, ct: transactionCt);

            return true;
        }, ct);

        if (failure is not null)
            return failure.LogFailure(logger, "AdjustDailyEntry");

        logger.LogInformation(
            "Daily entry {DailyEntryId} adjusted for flock {FlockId} on {EntryDate}",
            entry.Id, entry.FlockId, entry.Date);
        return Result.Success(new AdjustDailyEntryResponse(
            entry.Id, entry.Status.ToString(), entry.Version));
    }

    // "EggLot.SoldExceedsAdjusted" without the grade name would leave the
    // admin guessing which line is blocked.
    private async Task<Error> NameGradeAsync(Error error, Guid gradeId, CancellationToken ct)
    {
        var name = (await eggGrades.GetByIdAsync(gradeId, ct))?.Name ?? gradeId.ToString();
        return Error.Domain(error.Code, $"Grade '{name}': {error.Description}");
    }

    // A max-length reason would push the prefixed ledger note past the
    // BirdMovement limit and throw — truncate the note, never the reason.
    internal static string MovementNote(string prefix, string? reason)
    {
        var note = prefix + reason;
        return note.Length <= Cluckwork.Domain.Flocks.BirdMovement.MaxNoteLength
            ? note
            : note[..Cluckwork.Domain.Flocks.BirdMovement.MaxNoteLength];
    }
}

public sealed record AdjustDailyEntryResponse(Guid Id, string Status, int Version);
