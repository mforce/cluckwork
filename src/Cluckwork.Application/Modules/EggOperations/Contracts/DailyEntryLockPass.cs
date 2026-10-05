namespace Cluckwork.Application.Modules.EggOperations.Contracts;

[ModuleContract("EggOperations")]
public sealed record DailyEntryLockPass(
    IReadOnlyList<LockedDailyEntry> Locked, IReadOnlyList<RefusedDailyEntryLock> Refused);
