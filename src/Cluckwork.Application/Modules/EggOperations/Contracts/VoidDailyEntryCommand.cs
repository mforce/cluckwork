namespace Cluckwork.Application.Modules.EggOperations.Contracts;

public sealed record VoidDailyEntryCommand(
    Guid DailyEntryId,
    int Version,
    string Reason);
