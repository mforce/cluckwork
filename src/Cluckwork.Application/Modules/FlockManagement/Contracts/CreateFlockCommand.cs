namespace Cluckwork.Application.Modules.FlockManagement.Contracts;

// Farm and House have no aggregates yet, so for the MVP a flock is always
// created under the seeded default farm/house — the client cannot supply
// arbitrary (and unvalidated) farm/house ids. Add farm/house selection once
// those aggregates + their tenant scoping exist.
[ModuleContract("FlockManagement")]
public sealed record CreateFlockCommand(
    string Name,
    string Breed,
    DateOnly PlacementDate,
    int InitialCount);
