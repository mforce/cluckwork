namespace Cluckwork.Application.Modules.EggOperations.Contracts;

[ModuleContract("EggOperations")]
public sealed record VoidDailyEntryResponse(Guid Id, string Status, int Version);
