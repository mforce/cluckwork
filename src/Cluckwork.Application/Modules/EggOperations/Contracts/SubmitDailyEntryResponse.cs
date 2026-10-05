namespace Cluckwork.Application.Modules.EggOperations.Contracts;

public sealed record SubmitDailyEntryResponse(Guid Id, string Status, IReadOnlyList<Guid> EggLotIds);
