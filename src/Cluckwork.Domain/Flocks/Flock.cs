namespace Cluckwork.Domain.Flocks;

public sealed class Flock : AggregateRoot<Guid>
{
    public Guid FarmId { get; private set; }
    public Guid HouseId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Breed { get; private set; } = string.Empty;
    public DateOnly PlacementDate { get; private set; }
    public int InitialCount { get; private set; }
    public FlockStatus Status { get; private set; }

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
            Name = name, Breed = breed,
            PlacementDate = placementDate,
            InitialCount = initialCount,
            Status = FlockStatus.Active
        };
    }

    public Result Deplete()
    {
        if (Status != FlockStatus.Active)
            return Result.Failure(Error.Domain("Flock.NotActive", "Only active flocks can be depleted."));
        Status = FlockStatus.Depleted;
        return Result.Success();
    }
}

public enum FlockStatus { Active, Depleted, Archived }
