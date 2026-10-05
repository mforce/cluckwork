namespace Cluckwork.Application.Features.DailyEntries.RecordDailyEntry;

[ModuleContract("EggOperations")]
public sealed record RecordDailyEntryCommand(
    Guid FarmId,
    Guid HouseId,
    Guid FlockId,
    DateOnly Date,
    int TotalEggs,
    int CrackedEggs,
    int DirtyEggs,
    int DiscardedEggs,
    int MortalityCount,
    IReadOnlyList<GradeQuantityDto>? Grades = null);

// Sellable production for one grade, referencing an EggGrade row (spec §9.2).
[ModuleContract("EggOperations")]
public sealed record GradeQuantityDto(Guid EggGradeId, int Quantity);
