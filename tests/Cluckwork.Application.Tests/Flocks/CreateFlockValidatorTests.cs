namespace Cluckwork.Application.Tests.Flocks;

using Cluckwork.Application.Features.Flocks.CreateFlock;
using Cluckwork.Application.Tests.Common;

public sealed class CreateFlockValidatorTests
{
    private static readonly DateOnly FarmToday = FixedFarmClock.Today;
    private readonly CreateFlockValidator _validator = new(FixedFarmClock.AtDefault());

    private static CreateFlockCommand Valid() => new(
        Name: "House 1 layers", Breed: "ISA Brown",
        PlacementDate: FarmToday, InitialCount: 500);

    [Fact]
    public async Task ValidCommand_Passes()
    {
        Assert.True((await _validator.ValidateAsync(Valid())).IsValid);
    }

    [Fact]
    public async Task EmptyName_Fails()
    {
        var result = await _validator.ValidateAsync(Valid() with { Name = "" });
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateFlockCommand.Name));
    }

    [Fact]
    public async Task NonPositiveInitialCount_Fails()
    {
        var result = await _validator.ValidateAsync(Valid() with { InitialCount = 0 });
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateFlockCommand.InitialCount));
    }

    [Fact]
    public async Task FuturePlacementDate_Fails()
    {
        // Measured against the FARM's today (#155), not the build machine's.
        var result = await _validator.ValidateAsync(Valid() with { PlacementDate = FarmToday.AddDays(1) });
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateFlockCommand.PlacementDate));
    }

    [Fact]
    public async Task PlacementDateOfFarmToday_Passes()
    {
        Assert.True((await _validator.ValidateAsync(Valid() with { PlacementDate = FarmToday })).IsValid);
    }
}
