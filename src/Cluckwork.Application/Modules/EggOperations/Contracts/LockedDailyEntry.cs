namespace Cluckwork.Application.Modules.EggOperations.Contracts;

public sealed record LockedDailyEntry(Guid Id, Guid FlockId, DateOnly Date);
