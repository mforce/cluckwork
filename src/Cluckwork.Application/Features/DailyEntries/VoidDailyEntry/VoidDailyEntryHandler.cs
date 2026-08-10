namespace Cluckwork.Application.Features.DailyEntries.VoidDailyEntry;

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

// #69 (spec §8.1/§9.5) — void reverses everything the entry's submit created:
// each egg lot is emptied (blocked if any of its eggs were already sold), the
// day's mortality gets a compensating negative Adjustment ledger row, and the
// entry is preserved as Voided. All in one transaction; #60's provenance
// discipline (nothing deleted, compensating rows only).
public sealed class VoidDailyEntryHandler(
    IDailyEntryRepository entries,
    IEggLotRepository eggLots,
    IEggGradeRepository eggGrades,
    IBirdMovementRepository birdMovements,
    IEggInventoryMovementRepository eggMovements,
    IFlockRepository flocks,
    IClock clock,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    ILogger<VoidDailyEntryHandler> logger)
{
    public async Task<Result<VoidDailyEntryResponse>> HandleAsync(
        VoidDailyEntryCommand command, Guid accountId, CancellationToken ct)
    {
        var entry = await entries.GetByIdAsync(command.DailyEntryId, ct);
        if (entry is null)
            return Result.Failure<VoidDailyEntryResponse>(
                Error.NotFound(nameof(DailyEntry), command.DailyEntryId)).LogFailure(logger, "VoidDailyEntry");

        if (entry.Version != command.Version)
            return Result.Failure<VoidDailyEntryResponse>(Error.Conflict(
                "DailyEntry.VersionMismatch",
                "The entry was changed by someone else. Reload it and retry.")).LogFailure(logger, "VoidDailyEntry");

        var flock = await flocks.GetByIdAsync(entry.FlockId, ct);
        if (flock is null)
            return Result.Failure<VoidDailyEntryResponse>(
                Error.NotFound(nameof(Flock), entry.FlockId)).LogFailure(logger, "VoidDailyEntry");
        if (!flock.CanRecordProductionOn(entry.Date))
            return Result.Failure<VoidDailyEntryResponse>(Error.Validation(
                "DailyEntry.FlockNotActive",
                $"Flock '{flock.Name}' is {flock.Status.ToString().ToLowerInvariant()} — this entry can no longer be voided."))
                .LogFailure(logger, "VoidDailyEntry");

        var mortalityToReverse = entry.MortalityCount;
        var hadGradeLines = entry.Grades.Count > 0;
        Result<VoidDailyEntryResponse>? failure = null;

        await unitOfWork.ExecuteInTransactionAsync(async transactionCt =>
        {
            var lockedLots = await eggLots.GetByDailyEntryLockedAsync(
                accountId, entry.Id, transactionCt);

            // Same pre-linkage guard as adjust: grade lines but no linked lots
            // means we cannot know which lots to reverse.
            if (hadGradeLines && lockedLots.Count == 0)
            {
                failure = Result.Failure<VoidDailyEntryResponse>(Error.Domain(
                    "DailyEntry.PredatesLotTracking",
                    "This entry predates lot-to-entry tracking and cannot be voided."));
                return false;
            }

            var voided = entry.Void(command.Reason);
            if (voided.IsFailure)
            {
                failure = Result.Failure<VoidDailyEntryResponse>(voided.Error);
                return false;
            }

            foreach (var lot in lockedLots)
            {
                if (lot.QuantityProduced == 0) continue;
                var vacated = lot.QuantityAvailable;
                var emptied = lot.AdjustProduction(0);
                if (emptied.IsFailure)
                {
                    var name = (await eggGrades.GetByIdAsync(lot.EggGradeId, transactionCt))?.Name
                        ?? lot.EggGradeId.ToString();
                    failure = Result.Failure<VoidDailyEntryResponse>(Error.Domain(
                        emptied.Error.Code, $"Grade '{name}': {emptied.Error.Description}"));
                    return false;
                }

                // Ledger row (#101): the vacated eggs leave as an explicit Void
                // movement, same transaction.
                if (vacated > 0)
                    await eggMovements.AddAsync(EggInventoryMovement.Create(
                        Guid.NewGuid(), accountId, lot.Id, EggMovementType.Void,
                        -vacated, nameof(DailyEntry), entry.Id, clock.UtcNow,
                        reason: command.Reason), transactionCt);
            }

            // The submit-generated Mortality row stays; a negative Adjustment
            // tied to the entry puts the birds back on the ledger.
            if (mortalityToReverse > 0)
            {
                await birdMovements.AddAsync(BirdMovement.Create(
                    Guid.NewGuid(), accountId, entry.FlockId, entry.Date,
                    BirdMovementType.Adjustment, -mortalityToReverse,
                    note: AdjustDailyEntry.AdjustDailyEntryHandler.MovementNote(
                        "Entry voided: ", entry.VoidReason),
                    dailyEntryId: entry.Id), transactionCt);
            }

            // Same transaction as the change (#93): rolls back with it.
            await audit.WriteAsync(AuditActions.DailyEntryVoid, nameof(DailyEntry), entry.Id,
                command.Reason, ct: transactionCt);

            return true;
        }, ct);

        if (failure is not null)
            return failure.LogFailure(logger, "VoidDailyEntry");

        logger.LogInformation(
            "Daily entry {DailyEntryId} voided for flock {FlockId} on {EntryDate}: {MortalityReversed} mortality reversed",
            entry.Id, entry.FlockId, entry.Date, mortalityToReverse);
        return Result.Success(new VoidDailyEntryResponse(
            entry.Id, entry.Status.ToString(), entry.Version));
    }
}

public sealed record VoidDailyEntryResponse(Guid Id, string Status, int Version);
