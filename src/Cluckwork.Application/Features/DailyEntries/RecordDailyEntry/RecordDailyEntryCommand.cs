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
    int MortalityCount);
