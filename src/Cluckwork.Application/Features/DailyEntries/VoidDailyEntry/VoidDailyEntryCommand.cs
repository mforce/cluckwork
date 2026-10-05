namespace Cluckwork.Application.Features.DailyEntries.VoidDailyEntry;

[ModuleContract("EggOperations")]
public sealed record VoidDailyEntryCommand(
    Guid DailyEntryId,
    int Version,
    string Reason);
