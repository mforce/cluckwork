namespace Cluckwork.Application.Tests.Flocks;

using Cluckwork.Application.Features.Flocks.CreateFlock;

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
    public void FuturePlacementDate_Fails()
    {
        var future = DateOnly.FromDateTime(DateTime.UtcNow.Date).AddDays(1);
        var result = _validator.Validate(Valid() with { PlacementDate = future });
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateFlockCommand.PlacementDate));
    }
}
