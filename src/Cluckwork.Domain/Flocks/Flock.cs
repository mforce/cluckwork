namespace Cluckwork.Domain.Flocks;

public sealed class Flock : AggregateRoot<Guid>
{
    public const int MaxNameLength = 100;
    public const int MaxBreedLength = 100;

    public Guid FarmId { get; private set; }
    public Guid HouseId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Breed { get; private set; } = string.Empty;
    public DateOnly PlacementDate { get; private set; }
    public int InitialCount { get; private set; }
    public FlockStatus Status { get; private set; }
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

        Name = name.Trim();
        Breed = breed.Trim();
        PlacementDate = placementDate;
        InitialCount = initialCount;
        Version++;
        return Result.Success();
    }

    public Result Deplete()
    {
        if (Status != FlockStatus.Active)
            return Result.Failure(Error.Domain("Flock.NotActive", "Only active flocks can be depleted."));
        Status = FlockStatus.Depleted;
        Version++;
        return Result.Success();
    }

    // Archive hides a flock from pickers and the dashboard. Allowed from Active
    // too (a mistake-created flock shouldn't need a fake depletion first).
    public Result Archive()
    {
        if (Status == FlockStatus.Archived)
            return Result.Failure(Error.Domain("Flock.AlreadyArchived", "Flock is already archived."));
        Status = FlockStatus.Archived;
        Version++;
        return Result.Success();
    }
}

public enum FlockStatus { Active, Depleted, Archived }
