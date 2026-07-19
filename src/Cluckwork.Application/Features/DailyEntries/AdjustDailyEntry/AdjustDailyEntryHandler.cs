namespace Cluckwork.Application.Features.DailyEntries.AdjustDailyEntry;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.DailyEntries;
using Cluckwork.Application.Features.EggGrades;
using Cluckwork.Application.Features.EggLots;
using Cluckwork.Application.Features.Flocks;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Eggs;
using Cluckwork.Domain.Flocks;

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
    IUnitOfWork unitOfWork)
{
    public async Task<Result<AdjustDailyEntryResponse>> HandleAsync(
        AdjustDailyEntryCommand command, Guid accountId, CancellationToken ct)
    {
        var entry = await entries.GetByIdAsync(command.DailyEntryId, ct);
        if (entry is null)
            return Result.Failure<AdjustDailyEntryResponse>(
                Error.NotFound(nameof(DailyEntry), command.DailyEntryId));

        // End-to-end optimistic concurrency (PR #77 contract): the client's
        // base version must match; the EF token backstops the save itself.
        if (entry.Version != command.Version)
            return Result.Failure<AdjustDailyEntryResponse>(Error.Conflict(
                "DailyEntry.VersionMismatch",
                "The entry was changed by someone else. Reload it and re-apply the adjustment."));

        // Archived flocks are read-only history — same gate as recording.
        // Depleted flocks accept corrections for dates up to their depletion.
        var flock = await flocks.GetByIdAsync(entry.FlockId, ct);
        if (flock is null)
            return Result.Failure<AdjustDailyEntryResponse>(
                Error.NotFound(nameof(Flock), entry.FlockId));
        if (!flock.CanRecordProductionOn(entry.Date))
            return Result.Failure<AdjustDailyEntryResponse>(Error.Validation(
                "DailyEntry.FlockNotActive",
                $"Flock '{flock.Name}' is {flock.Status.ToString().ToLowerInvariant()} — this entry can no longer be adjusted."));

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
                    "One or more egg grades do not exist, are inactive, or are not saleable."));
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
            // exist the first (canonical order) carries the line, the rest zero.
            var targets = entry.Grades.ToDictionary(l => l.EggGradeId, l => l.Quantity);
            var carried = new HashSet<Guid>();
            foreach (var lot in lockedLots)
            {
                var target = !carried.Add(lot.EggGradeId)
                    ? 0
                    : targets.GetValueOrDefault(lot.EggGradeId, 0);
                if (lot.QuantityProduced == target) continue;

                var result = lot.AdjustProduction(target);
                if (result.IsFailure)
                {
                    failure = Result.Failure<AdjustDailyEntryResponse>(
                        await NameGradeAsync(result.Error, lot.EggGradeId, transactionCt));
                    return false;
                }
            }

            // Grade lines the entry gained that never had a lot (grade added
            // by the adjustment) get one now, dated and linked like the rest.
            foreach (var (gradeId, quantity) in targets)
            {
                if (lockedLots.Any(l => l.EggGradeId == gradeId)) continue;
                await eggLots.AddAsync(EggLot.Create(
                    Guid.NewGuid(), accountId, entry.FlockId, entry.Date, gradeId, quantity,
                    dailyEntryId: entry.Id),
                    transactionCt);
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
                    note: "Entry adjusted: " + entry.AdjustReason,
                    dailyEntryId: entry.Id), transactionCt);
            }

            return true;
        }, ct);

        if (failure is not null)
            return failure;

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
}

public sealed record AdjustDailyEntryResponse(Guid Id, string Status, int Version);
