namespace Cluckwork.Application.Modules.EggOperations.Contracts;

public sealed record DailyEntryLockPass(
    IReadOnlyList<LockedDailyEntry> Locked, IReadOnlyList<RefusedDailyEntryLock> Refused);
