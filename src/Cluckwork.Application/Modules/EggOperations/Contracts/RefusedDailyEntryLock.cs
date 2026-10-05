namespace Cluckwork.Application.Modules.EggOperations.Contracts;

[ModuleContract("EggOperations")]
public sealed record RefusedDailyEntryLock(Guid Id, Error Error);
