using Cluckwork.Application.Common;

namespace Cluckwork.Application.Features.DailyEntries.LockDueDailyEntries;

// #69 — spec §8.1: a Submitted entry dated before the cutoff becomes Locked.
// DailyEntryLockSweep picks the account, the cutoff and the batch size, and
// logs what this returns. Idempotent: an entry is locked at most once.
public sealed class LockDueDailyEntriesHandler(
    IDailyEntryRepository entries,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<DailyEntryLockPass> HandleAsync(DateOnly before, int batchSize, CancellationToken ct)
    {
        var due = await entries.ListSubmittedBeforeAsync(before, batchSize, ct);
        if (due.Count == 0) return new DailyEntryLockPass([], []);

        var locked = new List<LockedDailyEntry>();
        var refused = new List<RefusedDailyEntryLock>();
        foreach (var entry in due)
        {
            var result = entry.Lock(clock.UtcNow);
            if (result.IsFailure)
                refused.Add(new RefusedDailyEntryLock(entry.Id, result.Error));
            else
                locked.Add(new LockedDailyEntry(entry.Id, entry.FlockId, entry.Date));
        }

        // A concurrent adjust on one of these entries wins the Version token
        // race; the whole batch retries on the next poll minus that entry.
        await unitOfWork.SaveChangesAsync(ct);
        return new DailyEntryLockPass(locked, refused);
    }
}

public sealed record DailyEntryLockPass(
    IReadOnlyList<LockedDailyEntry> Locked, IReadOnlyList<RefusedDailyEntryLock> Refused);

public sealed record LockedDailyEntry(Guid Id, Guid FlockId, DateOnly Date);

public sealed record RefusedDailyEntryLock(Guid Id, Error Error);
