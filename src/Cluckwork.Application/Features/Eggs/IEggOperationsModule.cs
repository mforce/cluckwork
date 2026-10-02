using Cluckwork.Application.Features.DailyEntries.AdjustDailyEntry;
using Cluckwork.Application.Features.DailyEntries.LockDueDailyEntries;
using Cluckwork.Application.Features.DailyEntries.RecordDailyEntry;
using Cluckwork.Application.Features.DailyEntries.SubmitDailyEntry;
using Cluckwork.Application.Features.DailyEntries.VoidDailyEntry;
using Cluckwork.Application.Features.EggGrades.CreateEggGrade;
using Cluckwork.Application.Features.EggGrades.UpdateEggGrade;
using Cluckwork.Application.Features.EggLots;
using Cluckwork.Application.Features.EggLots.RecordEggLotMovement;
using Cluckwork.Domain.Eggs;

namespace Cluckwork.Application.Features.Eggs;

// #853: the Egg Operations contract for adapters. Adapters reach Egg Operations
// only through the types module-ledger.json lists under
// owners.EggOperations.contract; peer modules use the narrower IEggGradeLookup
// and IDailyEntryLookup ports.
public interface IEggOperationsModule
{
    // The EntityType Egg Operations writes on its audit rows; provenance reads key by it.
    const string DailyEntryAuditEntityType = nameof(DailyEntry);
    const string EggGradeAuditEntityType = nameof(EggGrade);

    Task<Result<Guid>> RecordDailyEntryAsync(RecordDailyEntryCommand command, Guid accountId, CancellationToken ct);

    Task<Result<SubmitDailyEntryResponse>> SubmitDailyEntryAsync(Guid id, Guid accountId, CancellationToken ct);

    Task<Result<AdjustDailyEntryResponse>> AdjustDailyEntryAsync(
        AdjustDailyEntryCommand command, Guid accountId, CancellationToken ct);

    Task<Result<VoidDailyEntryResponse>> VoidDailyEntryAsync(
        VoidDailyEntryCommand command, Guid accountId, CancellationToken ct);

    Task<DailyEntryDetails?> GetDailyEntryAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<DailyEntryDetails>> ListDailyEntriesAsync(
        Guid? flockId, DateOnly? from, DateOnly? to, int limit, int offset, CancellationToken ct);

    // Locks up to batchSize Submitted entries dated before the cutoff, then saves.
    Task<DailyEntryLockPass> LockSubmittedEntriesAsync(DateOnly before, int batchSize, CancellationToken ct);

    Task<IReadOnlyList<EggGradeDetails>> ListActiveGradesAsync(Guid? farmId, CancellationToken ct);

    Task<IReadOnlyList<EggGradeDetails>> ListAllGradesAsync(CancellationToken ct);

    Task<EggGradeDetails?> GetGradeAsync(Guid id, CancellationToken ct);

    Task<Result<Guid>> CreateGradeAsync(CreateEggGradeCommand command, Guid accountId, CancellationToken ct);

    Task<Result> UpdateGradeAsync(UpdateEggGradeCommand command, CancellationToken ct);

    Task<Result> SetGradeActiveAsync(Guid id, bool active, CancellationToken ct);

    Task<IReadOnlyList<StockByGrade>> GetStockByGradeAsync(DateOnly asOfDate, CancellationToken ct);

    Task<IReadOnlyList<EggLotDetails>> ListLotsAsync(
        Guid? eggGradeId, DateOnly? from, DateOnly? to, int limit, int offset, CancellationToken ct);

    // Null when the lot does not exist or belongs to another farm.
    Task<IReadOnlyList<EggLotMovementDetails>?> ListLotMovementsAsync(Guid lotId, CancellationToken ct);

    Task<Result<RecordEggLotMovementResult>> RecordLotMovementAsync(
        RecordEggLotMovementCommand command, Guid accountId, CancellationToken ct);
}

public sealed record DailyEntryDetails(
    Guid Id, Guid FarmId, Guid HouseId, Guid FlockId, DateOnly Date, DailyEntryStatus Status,
    int TotalEggs, int CrackedEggs, int DirtyEggs, int DiscardedEggs, int MortalityCount,
    Guid? CrackedGradeId, Guid? DirtyGradeId, IReadOnlyList<GradeQuantityDto> Grades,
    int Version, string? AdjustReason, string? VoidReason, DateTimeOffset? LockedAtUtc,
    string? AdjustedFromJson);

public sealed record EggGradeDetails(
    Guid Id, Guid FarmId, string Name, EggGradeType GradeType, int SortOrder, bool IsSaleable,
    DailyEntryKind DailyEntryKind, bool Active, int? LowStockFloor);

public sealed record EggLotDetails(
    Guid Id, Guid EggGradeId, DateOnly ProductionDate, int QuantityProduced, int QuantityAvailable,
    DateOnly? RestrictedUntil, Guid? DailyEntryId);

public sealed record EggLotMovementDetails(
    Guid Id, EggMovementType MovementType, int QuantityDelta, string ReferenceType, Guid ReferenceId,
    string? Reason, DateTimeOffset CreatedAtUtc);
