using Cluckwork.Application.Common;

namespace Cluckwork.Application.Features.DailyEntries.LockDueDailyEntries;

[ModuleContract("EggOperations")]
public sealed record DailyEntryLockPass(
    IReadOnlyList<LockedDailyEntry> Locked, IReadOnlyList<RefusedDailyEntryLock> Refused);
