namespace Cluckwork.Application.Features.Flocks.CreateFlock;

using Cluckwork.Domain.Accounts;

public sealed record CreateFlockCommand(
    string Name,
    string Breed,
    DateOnly PlacementDate,
    int InitialCount,
    Guid? FarmId = null,
    Guid? HouseId = null)
{
    // Farm/House have no aggregates yet — default to the seeded well-known ids.
    public Guid ResolvedFarmId => FarmId ?? SeedDefaults.FarmId;
    public Guid ResolvedHouseId => HouseId ?? SeedDefaults.HouseId;
}
