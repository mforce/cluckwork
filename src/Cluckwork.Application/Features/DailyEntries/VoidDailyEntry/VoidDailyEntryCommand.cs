namespace Cluckwork.Application.Features.DailyEntries.VoidDailyEntry;

public sealed record VoidDailyEntryCommand(
    Guid DailyEntryId,
    int Version,
    string Reason);
