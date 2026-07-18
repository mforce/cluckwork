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
        var result = flock.Deplete(new DateOnly(2026, 7, 16));
        Assert.True(result.IsSuccess);
        Assert.Equal(FlockStatus.Depleted, flock.Status);
    }

    [Fact]
    public void Deplete_WhenAlreadyDepleted_Fails()
    {
        var flock = Make();
        flock.Deplete(new DateOnly(2026, 7, 16));

        var result = flock.Deplete(new DateOnly(2026, 7, 16));
        Assert.True(result.IsFailure);
        Assert.Equal("Flock.NotActive", result.Error.Code);
    }

    [Fact]
    public void Update_ChangesFields_AndBumpsVersion()
    {
        var flock = Make();
        var result = flock.Update("  House 2  ", "Lohmann", new DateOnly(2026, 1, 15), 450);

        Assert.True(result.IsSuccess);
        Assert.Equal("House 2", flock.Name);          // trimmed
        Assert.Equal("Lohmann", flock.Breed);
        Assert.Equal(new DateOnly(2026, 1, 15), flock.PlacementDate);
        Assert.Equal(450, flock.InitialCount);
        Assert.Equal(1, flock.Version);
    }

    [Fact]
    public void Update_WithBlankName_Fails()
    {
        var flock = Make();
        var result = flock.Update("   ", "Lohmann", new DateOnly(2026, 1, 15), 450);
        Assert.True(result.IsFailure);
        Assert.Equal("Flock.NameRequired", result.Error.Code);
        Assert.Equal(0, flock.Version);
    }

    [Fact]
    public void Archive_FromActiveOrDepleted_Succeeds_TwiceFails()
    {
        var active = Make();
        Assert.True(active.Archive(new DateOnly(2026, 7, 16)).IsSuccess);

        var depleted = Make();
        depleted.Deplete(new DateOnly(2026, 7, 16));
        Assert.True(depleted.Archive(new DateOnly(2026, 7, 16)).IsSuccess);
        Assert.Equal(FlockStatus.Archived, depleted.Status);

        var again = depleted.Archive(new DateOnly(2026, 7, 16));
        Assert.True(again.IsFailure);
        Assert.Equal("Flock.AlreadyArchived", again.Error.Code);
    }

    [Fact]
    public void Reactivate_RestoresActive_ClearsStamps_BumpsVersion()
    {
        var depleted = Make();
        depleted.Deplete(new DateOnly(2026, 7, 10));
        var result = depleted.Reactivate();
        Assert.True(result.IsSuccess);
        Assert.Equal(FlockStatus.Active, depleted.Status);
        Assert.Null(depleted.DepletedOn);
        Assert.Equal(2, depleted.Version);
        // Full capture restored: dates after the old depletion work again.
        Assert.True(depleted.CanRecordProductionOn(new DateOnly(2026, 7, 20)));

        var archived = Make();
        archived.Archive(new DateOnly(2026, 7, 10));
        Assert.True(archived.Reactivate().IsSuccess);
        Assert.Null(archived.ArchivedOn);

        var active = Make();
        var already = active.Reactivate();
        Assert.True(already.IsFailure);
        Assert.Equal("Flock.AlreadyActive", already.Error.Code);
    }

    [Fact]
    public void CanRecordProductionOn_RespectsLifecycleDates()
    {
        var flock = Make();
        var depletedOn = new DateOnly(2026, 7, 10);

        Assert.True(flock.CanRecordProductionOn(new DateOnly(2026, 7, 20)));   // active: any date

        flock.Deplete(depletedOn);
        Assert.True(flock.CanRecordProductionOn(depletedOn));                  // backfill: on the day
        Assert.True(flock.CanRecordProductionOn(depletedOn.AddDays(-3)));      // backfill: before
        Assert.False(flock.CanRecordProductionOn(depletedOn.AddDays(1)));      // after depletion

        flock.Archive(depletedOn.AddDays(5));
        Assert.False(flock.CanRecordProductionOn(depletedOn.AddDays(-3)));     // archived: never
    }

    [Fact]
    public void LifecycleMutations_BumpVersion()
    {
        // AGENTS.md rule: every aggregate mutation bumps the concurrency token.
        var flock = Make();
        flock.Deplete(new DateOnly(2026, 7, 16));
        Assert.Equal(1, flock.Version);
        flock.Archive(new DateOnly(2026, 7, 16));
        Assert.Equal(2, flock.Version);
    }

    [Fact]
    public void Create_WithBlankName_Throws()
    {
        Assert.Throws<ArgumentException>(() => Flock.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "  ", "ISA Brown", DateOnly.FromDateTime(DateTime.Today), 500));
    }

    [Fact]
    public void Create_WithNonPositiveCount_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Flock.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "House 1", "ISA Brown", DateOnly.FromDateTime(DateTime.Today), 0));
    }
}
