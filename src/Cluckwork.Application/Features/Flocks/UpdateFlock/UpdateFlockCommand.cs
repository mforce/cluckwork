namespace Cluckwork.Application.Features.Flocks.UpdateFlock;

[ModuleContract("FlockManagement")]
public sealed record UpdateFlockCommand(
    Guid FlockId,
    string Name,
    string Breed,
    DateOnly PlacementDate,
    int InitialCount);
