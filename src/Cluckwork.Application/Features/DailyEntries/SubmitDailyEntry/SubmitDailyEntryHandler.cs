namespace Cluckwork.Application.Features.DailyEntries.SubmitDailyEntry;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.EggGrades;
using Cluckwork.Application.Features.EggLots;
using Cluckwork.Application.Features.Eggs;
using Cluckwork.Application.Features.Flocks;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Eggs;
using Cluckwork.Domain.Flocks;
using Microsoft.Extensions.Logging;

// The production -> stock bridge (#8): submitting a daily entry turns its grade
// lines into egg lots, one lot per grade, dated by the entry, and its mortality
// count into a bird-movement ledger row (#54). Submit + lots + movement commit
// atomically; a failed save leaves the entry Draft with nothing generated.
// Duplicate submits are blocked by the state machine (NotDraft) and, under
// concurrency, by the entry's optimistic Version token — so lots and movements
// are never generated twice.
public sealed class SubmitDailyEntryHandler(
    IDailyEntryRepository entries,
    IEggGradeRepository eggGrades,
    IEggLotRepository eggLots,
    IBirdMovementRepository birdMovements,
    IEggInventoryMovementRepository eggMovements,
    IFlockRepository flocks,
    IFlockScopeGuard flockScope,
    IClock clock,
    IAuditWriter audit,
    IUnitOfWork unitOfWork,
    ILogger<SubmitDailyEntryHandler> logger)
{
    public async Task<Result<SubmitDailyEntryResponse>> HandleAsync(
        Guid dailyEntryId, Guid accountId, CancellationToken ct)
    {
        // #388 — this WRITE preserves the existing 422 scope-guard contract.
        // The repository bypasses the combined query filter but explicitly
        // reinstates AccountId, so an own-account unassigned draft reaches
        // FlockScopeGuard below while a foreign-account id still reads as null.
        // Ordinary GET reads use GetReadOnlyAsync and remain symmetric 404.
        var entry = await entries.GetByIdForFlockScopedWriteAsync(
            dailyEntryId, accountId, ct);
        if (entry is null)
            return Result.Failure<SubmitDailyEntryResponse>(
                Error.NotFound(nameof(DailyEntry), dailyEntryId)).LogFailure(logger, "SubmitDailyEntry");

        // Same lifecycle gate as recording (#47/#54): a draft can still be
        // submitted after depletion when its date is on/before DepletedOn
        // (late backfill), but never for an archived flock.
        var flock = await flocks.GetByIdAsync(entry.FlockId, ct);
        if (flock is not null && !flock.CanRecordProductionOn(entry.Date))
            return Result.Failure<SubmitDailyEntryResponse>(Error.Validation(
                "DailyEntry.FlockNotActive",
                $"Flock '{flock.Name}' is {flock.Status.ToString().ToLowerInvariant()} — this entry can no longer be submitted."))
                .LogFailure(logger, "SubmitDailyEntry");

        // Spec §5.3 (#103): submitting is recording too — same scope rule.
        var scope = await flockScope.CheckAsync(entry.FlockId, ct);
        if (scope.IsFailure)
            return Result.Failure<SubmitDailyEntryResponse>(scope.Error).LogFailure(logger, "SubmitDailyEntry");

        // #396 — submission is the ONE point at which the Cracked and Dirty
        // counters resolve to a grade. Both flags are required and neither
        // implies the other: EggGrade.Deactivate() leaves IsSaleable set, so
        // "inactive but saleable" is an ordinary reachable state, and resolving
        // on saleability alone would mint stock under a grade the farm has
        // already retired from capture. Unresolved (null) is a durable record
        // that those eggs were a loss, not a gap to be filled in later — see
        // DailyEntry.CrackedGradeId.
        var farmGrades = await eggGrades.ListActiveAsync(entry.FarmId, ct);
        var crackedGradeId = ResolveCondition(farmGrades, DailyEntryKind.Cracked);
        var dirtyGradeId = ResolveCondition(farmGrades, DailyEntryKind.Dirty);

        var submit = entry.Submit(crackedGradeId, dirtyGradeId);
        if (submit.IsFailure)
            return Result.Failure<SubmitDailyEntryResponse>(submit.Error).LogFailure(logger, "SubmitDailyEntry");

        var lotIds = new List<Guid>();
        foreach (var line in entry.Grades)
        {
            var lot = EggLot.Create(
                Guid.NewGuid(), accountId, entry.FlockId,
                entry.Date, line.EggGradeId, line.Quantity,
                dailyEntryId: entry.Id);
            await eggLots.AddAsync(lot, ct);
            lotIds.Add(lot.Id);

            // Ledger row (#101): the lot's opening balance as an explicit
            // Production movement — same transaction, so the cached
            // QuantityAvailable always equals the sum of movements.
            await eggMovements.AddAsync(EggInventoryMovement.Create(
                Guid.NewGuid(), accountId, lot.Id, EggMovementType.Production,
                line.Quantity, nameof(DailyEntry), entry.Id, clock.UtcNow), ct);
        }

        // #396 — the counter-backed lots, in the same transaction as the manual
        // ones. Read from the entry's own SNAPSHOT rather than from the ids
        // resolved above, so this loop and every later reader agree by
        // construction and an adjustment (which never re-resolves) takes the
        // identical path.
        //
        // A zero counter is skipped but stays snapshotted: EggLot.Create rejects
        // a zero quantity, and "the farm was selling cracked eggs that day" is
        // still a fact worth recording. Snapshot and lot are separate decisions.
        foreach (var (gradeId, quantity) in new[]
        {
            (entry.CrackedGradeId, entry.CrackedEggs),
            (entry.DirtyGradeId, entry.DirtyEggs),
        })
        {
            if (gradeId is null || quantity <= 0) continue;

            var lot = EggLot.Create(
                Guid.NewGuid(), accountId, entry.FlockId,
                entry.Date, gradeId.Value, quantity,
                dailyEntryId: entry.Id);
            await eggLots.AddAsync(lot, ct);
            lotIds.Add(lot.Id);

            await eggMovements.AddAsync(EggInventoryMovement.Create(
                Guid.NewGuid(), accountId, lot.Id, EggMovementType.Production,
                quantity, nameof(DailyEntry), entry.Id, clock.UtcNow), ct);
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

        // #494 — appended to THIS unit of work, so the event commits with the
        // lots and movements or not at all. Placed after every failure return
        // above: a submit that never happened must leave no trace.
        await audit.WriteAsync(
            AuditActions.DailyEntrySubmit, nameof(DailyEntry), entry.Id, ct: ct);

        // A concurrent submit that loses the Version race throws
        // DbUpdateConcurrencyException here; the API's global error handler maps it
        // to 409 and nothing from the losing request is persisted. A retry then
        // gets the state-machine 422 (NotDraft).
        await unitOfWork.SaveChangesAsync(ct);
        logger.LogInformation(
            "Daily entry {DailyEntryId} submitted for flock {FlockId} on {EntryDate}: {LotCount} egg lots, {MortalityCount} mortality",
            entry.Id, entry.FlockId, entry.Date, lotIds.Count, entry.MortalityCount);
        return Result.Success(new SubmitDailyEntryResponse(entry.Id, entry.Status.ToString(), lotIds));
    }

    // BOTH flags, never one. ListActiveAsync has already excluded inactive
    // grades; IsSaleable is checked here, and the pair is what the rule
    // requires. Kept as a named method so the requirement reads as one thing
    // rather than as an incidental filter clause.
    private static Guid? ResolveCondition(IReadOnlyList<EggGrade> activeFarmGrades, DailyEntryKind kind) =>
        activeFarmGrades.SingleOrDefault(g => g.DailyEntryKind == kind && g.IsSaleable)?.Id;
}

public sealed record SubmitDailyEntryResponse(Guid Id, string Status, IReadOnlyList<Guid> EggLotIds);
