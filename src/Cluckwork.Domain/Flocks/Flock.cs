namespace Cluckwork.Domain.Flocks;

public sealed class Flock : AggregateRoot<Guid>
{
    // Match the column widths (Name 200 / Breed 100) so validators and schema
    // agree on one limit.
    public const int MaxNameLength = 200;
    public const int MaxBreedLength = 100;

    public Guid FarmId { get; private set; }
    public Guid HouseId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Breed { get; private set; } = string.Empty;
    public DateOnly PlacementDate { get; private set; }
    public int InitialCount { get; private set; }
    public FlockStatus Status { get; private set; }
    // Lifecycle stamps: the operational date the action was taken (farm-local ≈
    // UTC for the MVP, issue #35). DepletedOn lets historical daily entries
    // dated on/before it stay recordable after depletion.
    public DateOnly? DepletedOn { get; private set; }
    public DateOnly? ArchivedOn { get; private set; }
    public int Version { get; private set; }

    private Flock() { }

    public static Flock Create(
        Guid id, Guid accountId, Guid farmId, Guid houseId,
        string name, string breed, DateOnly placementDate, int initialCount)
    {
        // Invariants: enforced here so no caller (handler, seeder, test) can build
        // an invalid aggregate. The FluentValidation validator is the user-facing
        // message surface; these guard against programmer error.
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Flock name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(breed))
            throw new ArgumentException("Flock breed is required.", nameof(breed));
        if (initialCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(initialCount), "Initial count must be positive.");

        return new Flock
        {
            Id = id, AccountId = accountId,
            FarmId = farmId, HouseId = houseId,
            Name = name.Trim(), Breed = breed.Trim(),
            PlacementDate = placementDate,
            InitialCount = initialCount,
            Status = FlockStatus.Active
        };
    }

    // Corrections to the identity fields (typos, wrong placement date/count).
    // Status is not touched here — lifecycle moves through Deplete/Archive.
    public Result Update(string name, string breed, DateOnly placementDate, int initialCount)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure(Error.Validation("Flock.NameRequired", "Flock name is required."));
        if (string.IsNullOrWhiteSpace(breed))
            return Result.Failure(Error.Validation("Flock.BreedRequired", "Flock breed is required."));
        if (initialCount <= 0)
            return Result.Failure(Error.Validation("Flock.CountInvalid", "Initial count must be positive."));
        if (placementDate == default)
            return Result.Failure(Error.Validation("Flock.PlacementRequired", "Placement date is required."));

        Name = name.Trim();
        Breed = breed.Trim();
        PlacementDate = placementDate;
        InitialCount = initialCount;
        Version++;
        return Result.Success();
    }

    public Result Deplete(DateOnly asOf)
    {
        if (Status != FlockStatus.Active)
            return Result.Failure(Error.Domain("Flock.NotActive", "Only active flocks can be depleted."));
        Status = FlockStatus.Depleted;
        DepletedOn = asOf;
        Version++;
        return Result.Success();
    }

    // Archive hides a flock from pickers and the dashboard. Allowed from Active
    // too (a mistake-created flock shouldn't need a fake depletion first).
    public Result Archive(DateOnly asOf)
    {
        if (Status == FlockStatus.Archived)
            return Result.Failure(Error.Domain("Flock.AlreadyArchived", "Flock is already archived."));
        Status = FlockStatus.Archived;
        ArchivedOn = asOf;
        Version++;
        return Result.Success();
    }

    // Undo for a mistaken deplete/archive (#57). Clearing the lifecycle stamps
    // restores full capture: the backfill window disappears with them.
    public Result Reactivate()
    {
        if (Status == FlockStatus.Active)
            return Result.Failure(Error.Domain("Flock.AlreadyActive", "Flock is already active."));
        Status = FlockStatus.Active;
        DepletedOn = null;
        ArchivedOn = null;
        Version++;
        return Result.Success();
    }

    // Whether production may be recorded for the given operational date:
    // active flocks always; depleted flocks only for dates on/before the
    // depletion date (late backfill of the final laying days); archived never.
    public bool CanRecordProductionOn(DateOnly date) => Status switch
    {
        FlockStatus.Active => true,
        FlockStatus.Depleted => DepletedOn is null || date <= DepletedOn,
        _ => false,
    };
}

public enum FlockStatus { Active, Depleted, Archived }
