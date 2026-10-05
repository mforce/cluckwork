using Cluckwork.Application.Common;

namespace Cluckwork.Application.Features.DailyEntries.LockDueDailyEntries;

[ModuleContract("EggOperations")]
public sealed record RefusedDailyEntryLock(Guid Id, Error Error);
