namespace Cluckwork.Application.Modules.EggOperations.Contracts;

[ModuleContract("EggOperations")]
public sealed record LockedDailyEntry(Guid Id, Guid FlockId, DateOnly Date);
