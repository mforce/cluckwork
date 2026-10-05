namespace Cluckwork.Application.Modules.EggOperations.Contracts;

[ModuleContract("EggOperations")]
public sealed record SubmitDailyEntryResponse(Guid Id, string Status, IReadOnlyList<Guid> EggLotIds);
