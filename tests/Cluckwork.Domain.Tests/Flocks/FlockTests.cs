namespace Cluckwork.Domain.Tests.Flocks;

using Cluckwork.Domain.Flocks;

public sealed class FlockTests
{
    private static Flock Make() => Flock.Create(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        "House 1 layers", "ISA Brown",
        DateOnly.FromDateTime(DateTime.Today), 500);

    [Fact]
    public void Create_StartsActive()
    {
        var flock = Make();
        Assert.Equal(FlockStatus.Active, flock.Status);
        Assert.Equal(500, flock.InitialCount);
    }

    [Fact]
    public void Deplete_WhenActive_Succeeds()
    {
        var flock = Make();
        var result = flock.Deplete();
        Assert.True(result.IsSuccess);
        Assert.Equal(FlockStatus.Depleted, flock.Status);
    }

    [Fact]
    public void Deplete_WhenAlreadyDepleted_Fails()
    {
        var flock = Make();
        flock.Deplete();

        var result = flock.Deplete();
        Assert.True(result.IsFailure);
        Assert.Equal("Flock.NotActive", result.Error.Code);
    }
}
