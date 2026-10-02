using Cluckwork.Application.Features.DailyEntries;
using Cluckwork.Application.Features.DailyEntries.AdjustDailyEntry;
using Cluckwork.Application.Features.DailyEntries.LockDueDailyEntries;
using Cluckwork.Application.Features.DailyEntries.RecordDailyEntry;
using Cluckwork.Application.Features.DailyEntries.SubmitDailyEntry;
using Cluckwork.Application.Features.DailyEntries.VoidDailyEntry;
using Cluckwork.Application.Features.EggGrades;
using Cluckwork.Application.Features.EggGrades.CreateEggGrade;
using Cluckwork.Application.Features.EggGrades.SetEggGradeActive;
using Cluckwork.Application.Features.EggGrades.UpdateEggGrade;
using Cluckwork.Application.Features.EggLots;
using Cluckwork.Application.Features.EggLots.RecordEggLotMovement;
using Cluckwork.Domain.Eggs;

namespace Cluckwork.Application.Features.Eggs;

public sealed class EggOperationsModule(
    IDailyEntryRepository entries,
    IEggGradeRepository grades,
    IEggLotRepository lots,
    IEggInventoryMovementRepository movements,
    RecordDailyEntryHandler recordEntry,
    SubmitDailyEntryHandler submitEntry,
    AdjustDailyEntryHandler adjustEntry,
    VoidDailyEntryHandler voidEntry,
    LockDueDailyEntriesHandler lockDueEntries,
    CreateEggGradeHandler createGrade,
    UpdateEggGradeHandler updateGrade,
    SetEggGradeActiveHandler setGradeActive,
    RecordEggLotMovementHandler recordLotMovement) : IEggOperationsModule
{
    public Task<Result<Guid>> RecordDailyEntryAsync(RecordDailyEntryCommand command, Guid accountId, CancellationToken ct) =>
        recordEntry.HandleAsync(command, accountId, ct);

    public Task<Result<SubmitDailyEntryResponse>> SubmitDailyEntryAsync(Guid id, Guid accountId, CancellationToken ct) =>
        submitEntry.HandleAsync(id, accountId, ct);

    public Task<Result<AdjustDailyEntryResponse>> AdjustDailyEntryAsync(
        AdjustDailyEntryCommand command, Guid accountId, CancellationToken ct) =>
        adjustEntry.HandleAsync(command, accountId, ct);

    public Task<Result<VoidDailyEntryResponse>> VoidDailyEntryAsync(
        VoidDailyEntryCommand command, Guid accountId, CancellationToken ct) =>
        voidEntry.HandleAsync(command, accountId, ct);

    public async Task<DailyEntryDetails?> GetDailyEntryAsync(Guid id, CancellationToken ct) =>
        await entries.GetReadOnlyAsync(id, ct) is { } entry ? ToDetails(entry) : null;

    public async Task<IReadOnlyList<DailyEntryDetails>> ListDailyEntriesAsync(
        Guid? flockId, DateOnly? from, DateOnly? to, int limit, int offset, CancellationToken ct) =>
        (await entries.ListAsync(flockId, from, to, limit, offset, ct)).Select(ToDetails).ToList();

    public Task<DailyEntryLockPass> LockSubmittedEntriesAsync(DateOnly before, int batchSize, CancellationToken ct) =>
        lockDueEntries.HandleAsync(before, batchSize, ct);

    public async Task<IReadOnlyList<EggGradeDetails>> ListActiveGradesAsync(Guid? farmId, CancellationToken ct) =>
        (await grades.ListActiveAsync(farmId, ct)).Select(ToDetails).ToList();

    public async Task<IReadOnlyList<EggGradeDetails>> ListAllGradesAsync(CancellationToken ct) =>
        (await grades.ListAllAsync(ct)).Select(ToDetails).ToList();

    public async Task<EggGradeDetails?> GetGradeAsync(Guid id, CancellationToken ct) =>
        await grades.GetByIdAsync(id, ct) is { } grade ? ToDetails(grade) : null;

    public Task<Result<Guid>> CreateGradeAsync(CreateEggGradeCommand command, Guid accountId, CancellationToken ct) =>
        createGrade.HandleAsync(command, accountId, ct);

    public Task<Result> UpdateGradeAsync(UpdateEggGradeCommand command, CancellationToken ct) =>
        updateGrade.HandleAsync(command, ct);

    public Task<Result> SetGradeActiveAsync(Guid id, bool active, CancellationToken ct) =>
        setGradeActive.HandleAsync(id, active, ct);

    public Task<IReadOnlyList<StockByGrade>> GetStockByGradeAsync(DateOnly asOfDate, CancellationToken ct) =>
        lots.GetStockByGradeAsync(asOfDate, ct);

    public async Task<IReadOnlyList<EggLotDetails>> ListLotsAsync(
        Guid? eggGradeId, DateOnly? from, DateOnly? to, int limit, int offset, CancellationToken ct) =>
        (await lots.ListAsync(eggGradeId, from, to, limit, offset, ct)).Select(l => new EggLotDetails(
            l.Id, l.EggGradeId, l.ProductionDate, l.QuantityProduced,
            l.QuantityAvailable, l.RestrictedUntil, l.DailyEntryId)).ToList();

    public async Task<IReadOnlyList<EggLotMovementDetails>?> ListLotMovementsAsync(Guid lotId, CancellationToken ct)
    {
        if (await lots.GetByIdAsync(lotId, ct) is null) return null;
        return (await movements.ListByLotAsync(lotId, ct)).Select(m => new EggLotMovementDetails(
            m.Id, m.MovementType, m.QuantityDelta, m.ReferenceType, m.ReferenceId,
            m.Reason, m.CreatedAtUtc)).ToList();
    }

    public Task<Result<RecordEggLotMovementResult>> RecordLotMovementAsync(
        RecordEggLotMovementCommand command, Guid accountId, CancellationToken ct) =>
        recordLotMovement.HandleAsync(command, accountId, ct);

    private static DailyEntryDetails ToDetails(DailyEntry e) =>
        new(e.Id, e.FarmId, e.HouseId, e.FlockId, e.Date, e.Status,
            e.TotalEggs, e.CrackedEggs, e.DirtyEggs, e.DiscardedEggs, e.MortalityCount,
            e.CrackedGradeId, e.DirtyGradeId,
            e.Grades.Select(g => new GradeQuantityDto(g.EggGradeId, g.Quantity)).ToList(),
            e.Version, e.AdjustReason, e.VoidReason, e.LockedAtUtc, e.AdjustedFromJson);

    private static EggGradeDetails ToDetails(EggGrade g) =>
        new(g.Id, g.FarmId, g.Name, g.GradeType, g.SortOrder, g.IsSaleable,
            g.DailyEntryKind, g.Active, g.LowStockFloor);
}
