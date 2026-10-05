namespace Cluckwork.Application.Modules.EggOperations.Contracts;

public sealed record AdjustDailyEntryResponse(Guid Id, string Status, int Version);
