namespace Cluckwork.Application.Modules.FlockManagement.Contracts;

public sealed record UpdateFlockCommand(
    Guid FlockId,
    string Name,
    string Breed,
    DateOnly PlacementDate,
    int InitialCount);
