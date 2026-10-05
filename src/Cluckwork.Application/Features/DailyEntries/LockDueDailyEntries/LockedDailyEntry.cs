using Cluckwork.Application.Common;

namespace Cluckwork.Application.Features.DailyEntries.LockDueDailyEntries;

[ModuleContract("EggOperations")]
public sealed record LockedDailyEntry(Guid Id, Guid FlockId, DateOnly Date);
