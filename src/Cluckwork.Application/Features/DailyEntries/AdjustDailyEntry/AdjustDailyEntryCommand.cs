namespace Cluckwork.Application.Features.DailyEntries.AdjustDailyEntry;

using Cluckwork.Application.Features.DailyEntries.RecordDailyEntry;

// Version = the base the client loaded the entry at; a stale one gets a
// deterministic 409 instead of silently overwriting another admin's
// correction (same end-to-end contract as water corrections, PR #77).
public sealed record AdjustDailyEntryCommand(
    Guid DailyEntryId,
    int Version,
    int TotalEggs,
    int CrackedEggs,
    int DirtyEggs,
    int DiscardedEggs,
    int MortalityCount,
    string Reason,
    IReadOnlyList<GradeQuantityDto>? Grades);
