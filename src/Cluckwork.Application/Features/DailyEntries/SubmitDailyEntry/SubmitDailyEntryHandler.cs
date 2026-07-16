namespace Cluckwork.Application.Features.DailyEntries.SubmitDailyEntry;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.EggLots;
using Cluckwork.Application.Features.Flocks;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Eggs;
using Cluckwork.Domain.Flocks;

// The production -> stock bridge (#8): submitting a daily entry turns its grade
// lines into egg lots, one lot per grade, dated by the entry, and its mortality
// count into a bird-movement ledger row (#54). Submit + lots + movement commit
// atomically; a failed save leaves the entry Draft with nothing generated.
// Duplicate submits are blocked by the state machine (NotDraft) and, under
// concurrency, by the entry's optimistic Version token — so lots and movements
// are never generated twice.
public sealed class SubmitDailyEntryHandler(
    IDailyEntryRepository entries,
    IEggLotRepository eggLots,
    IBirdMovementRepository birdMovements,
    IFlockRepository flocks,
    IUnitOfWork unitOfWork)
{
    public async Task<Result<SubmitDailyEntryResponse>> HandleAsync(
        Guid dailyEntryId, Guid accountId, CancellationToken ct)
    {
        // Tenant query filter scopes the lookup — a foreign entry reads as null.
        var entry = await entries.GetByIdAsync(dailyEntryId, ct);
        if (entry is null)
            return Result.Failure<SubmitDailyEntryResponse>(
                Error.NotFound(nameof(DailyEntry), dailyEntryId));

        // Same lifecycle gate as recording (#47/#54): a draft can still be
        // submitted after depletion when its date is on/before DepletedOn
        // (late backfill), but never for an archived flock.
        var flock = await flocks.GetByIdAsync(entry.FlockId, ct);
        if (flock is not null && !flock.CanRecordProductionOn(entry.Date))
            return Result.Failure<SubmitDailyEntryResponse>(Error.Validation(
                "DailyEntry.FlockNotActive",
                $"Flock '{flock.Name}' is {flock.Status.ToString().ToLowerInvariant()} — this entry can no longer be submitted."));

        var submit = entry.Submit();
        if (submit.IsFailure)
            return Result.Failure<SubmitDailyEntryResponse>(submit.Error);

        var lotIds = new List<Guid>();
        foreach (var line in entry.Grades)
        {
            var lot = EggLot.Create(
                Guid.NewGuid(), accountId, entry.FlockId,
                entry.Date, line.EggGradeId, line.Quantity);
            await eggLots.AddAsync(lot, ct);
            lotIds.Add(lot.Id);
        }

        // The day's mortality becomes a ledger row so the flock's current count
        // reflects it. Zero-mortality days write nothing.
        if (entry.MortalityCount > 0)
        {
            await birdMovements.AddAsync(BirdMovement.Create(
                Guid.NewGuid(), accountId, entry.FlockId,
                entry.Date, BirdMovementType.Mortality, entry.MortalityCount,
                note: "Daily entry mortality",
                dailyEntryId: entry.Id), ct);
        }

        // A concurrent submit that loses the Version race throws
        // DbUpdateConcurrencyException here; the API's global error handler maps it
        // to 409 and nothing from the losing request is persisted. A retry then
        // gets the state-machine 422 (NotDraft).
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success(new SubmitDailyEntryResponse(entry.Id, entry.Status.ToString(), lotIds));
    }
}

public sealed record SubmitDailyEntryResponse(Guid Id, string Status, IReadOnlyList<Guid> EggLotIds);
