namespace Cluckwork.Application.Features.Flocks.UpdateFlock;

public sealed record UpdateFlockCommand(
    Guid FlockId,
    string Name,
    string Breed,
    DateOnly PlacementDate,
    int InitialCount);
