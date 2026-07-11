namespace Cluckwork.Domain.Eggs;

public sealed class DailyEntry : AggregateRoot<Guid>
{
    public Guid FarmId { get; private set; }
    public Guid HouseId { get; private set; }
    public Guid FlockId { get; private set; }
    public DateOnly Date { get; private set; }
    public DailyEntryStatus Status { get; private set; }

    public int TotalEggs { get; private set; }
    public int CrackedEggs { get; private set; }
    public int DirtyEggs { get; private set; }
    public int DiscardedEggs { get; private set; }
    public int MortalityCount { get; private set; }

    // Optimistic concurrency token — functional spec §10.9.1
    public int Version { get; private set; }

    private DailyEntry() { }

    // Unique constraint: (account_id, farm_id, house_id, flock_id, date)
    public static DailyEntry Create(
        Guid id, Guid accountId, Guid farmId, Guid houseId, Guid flockId, DateOnly date)
    {
        return new DailyEntry
        {
            Id = id, AccountId = accountId,
            FarmId = farmId, HouseId = houseId,
            FlockId = flockId, Date = date,
            Status = DailyEntryStatus.Draft
        };
    }

    public Result RecordProduction(
        int totalEggs, int cracked, int dirty, int discarded, int mortality)
    {
        if (Status is DailyEntryStatus.Locked or DailyEntryStatus.Voided)
            return Result.Failure(Error.Domain(
                "DailyEntry.Immutable", "Cannot modify a locked or voided entry."));

        TotalEggs = totalEggs;
        CrackedEggs = cracked;
        DirtyEggs = dirty;
        DiscardedEggs = discarded;
        MortalityCount = mortality;
        Version++;
        return Result.Success();
    }

    public Result Submit()
    {
        if (Status != DailyEntryStatus.Draft)
            return Result.Failure(Error.Domain(
                "DailyEntry.NotDraft", "Only draft entries can be submitted."));
        Status = DailyEntryStatus.Submitted;
        return Result.Success();
    }

    public Result Lock()
    {
        if (Status != DailyEntryStatus.Submitted)
            return Result.Failure(Error.Domain(
                "DailyEntry.NotSubmitted", "Only submitted entries can be locked."));
        Status = DailyEntryStatus.Locked;
        return Result.Success();
    }

    public Result ManagerAdjust(
        int totalEggs, int cracked, int dirty, int discarded, int mortality, string reason)
    {
        if (Status != DailyEntryStatus.Locked)
            return Result.Failure(Error.Domain(
                "DailyEntry.NotLocked", "Manager adjustments require a locked entry."));

        TotalEggs = totalEggs;
        CrackedEggs = cracked;
        DirtyEggs = dirty;
        DiscardedEggs = discarded;
        MortalityCount = mortality;
        Status = DailyEntryStatus.ManagerAdjusted;
        Version++;
        return Result.Success();
    }
}

public enum DailyEntryStatus { Draft, Submitted, Locked, ManagerAdjusted, Voided }
