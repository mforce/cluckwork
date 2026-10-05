namespace Cluckwork.Application.Modules.EggOperations.Contracts;

[ModuleContract("EggOperations")]
public sealed record VoidDailyEntryCommand(
    Guid DailyEntryId,
    int Version,
    string Reason);
