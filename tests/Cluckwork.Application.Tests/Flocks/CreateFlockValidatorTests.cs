namespace Cluckwork.Application.Tests.Flocks;

using Cluckwork.Application.Features.Flocks.CreateFlock;
using Cluckwork.Domain.Accounts;

public sealed class CreateFlockValidatorTests
{
    private readonly CreateFlockValidator _validator = new();

    private static CreateFlockCommand Valid() => new(
        Name: "House 1 layers", Breed: "ISA Brown",
        PlacementDate: DateOnly.FromDateTime(DateTime.Today), InitialCount: 500);

    [Fact]
    public void ValidCommand_Passes()
    {
        Assert.True(_validator.Validate(Valid()).IsValid);
    }

    [Fact]
    public void EmptyName_Fails()
    {
        var result = _validator.Validate(Valid() with { Name = "" });
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateFlockCommand.Name));
    }

    [Fact]
    public void NonPositiveInitialCount_Fails()
    {
        var result = _validator.Validate(Valid() with { InitialCount = 0 });
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateFlockCommand.InitialCount));
    }

    [Fact]
    public void UnsetFarmHouse_DefaultsToSeedIds()
    {
        var cmd = Valid();
        Assert.Equal(SeedDefaults.FarmId, cmd.ResolvedFarmId);
        Assert.Equal(SeedDefaults.HouseId, cmd.ResolvedHouseId);
    }
}
