namespace Cluckwork.Application.Modules.EggOperations.Contracts;

[ModuleContract("EggOperations")]
public sealed record AdjustDailyEntryResponse(Guid Id, string Status, int Version);
