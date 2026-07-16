namespace Cluckwork.Application.Features.DailyEntries.RecordDailyEntry;

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

// Sellable production for one grade (e.g. "A-Large", 220).
public sealed record GradeQuantityDto(string GradeCode, int Quantity);
