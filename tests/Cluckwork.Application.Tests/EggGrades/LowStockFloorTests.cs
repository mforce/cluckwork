namespace Cluckwork.Application.Tests.EggGrades;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.EggGrades;
using Cluckwork.Application.Features.EggGrades.CreateEggGrade;
using Cluckwork.Application.Features.EggGrades.UpdateEggGrade;
using Cluckwork.Application.Features.EggLots;
using Cluckwork.Domain.Accounts;

// #911 — the two rules the low-stock floor adds: who may move one, and what
// "below the floor" measures.
public sealed class LowStockFloorTests
{
    private sealed record Actor(IReadOnlyList<string> Roles, bool IsResolved = true) : ICurrentUser
    {
        public Guid UserId => Guid.NewGuid();
        public string Email => "actor@test.local";
    }

    [Fact]
    public void Only_an_owner_may_move_a_floor()
    {
        Assert.True(EggGradeFloorPolicy.MaySetFloor(new Actor([Roles.Owner])));
        Assert.False(EggGradeFloorPolicy.MaySetFloor(new Actor([Roles.Manager])));
        Assert.False(EggGradeFloorPolicy.MaySetFloor(new Actor([Roles.Sales])));
        // A plain worker carries no role claims at all.
        Assert.False(EggGradeFloorPolicy.MaySetFloor(new Actor([])));
    }

    [Fact]
    public void An_unresolved_actor_may_not_move_a_floor()
    {
        // Nothing sets a floor from a system caller today, so the policy fails
        // closed rather than reading Roles off an actor that was never resolved.
        Assert.False(EggGradeFloorPolicy.MaySetFloor(new Actor([Roles.Owner], IsResolved: false)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5000)]
    public async Task Validators_accept_a_non_negative_floor(int floor)
    {
        var create = await new CreateEggGradeValidator().ValidateAsync(
            new CreateEggGradeCommand("Large", "Size", 0, true, floor));
        var update = await new UpdateEggGradeValidator().ValidateAsync(
            new UpdateEggGradeCommand(Guid.NewGuid(), "Large", 0, true, floor));

        Assert.True(create.IsValid);
        Assert.True(update.IsValid);
    }

    [Fact]
    public async Task Validators_reject_a_negative_floor()
    {
        var create = await new CreateEggGradeValidator().ValidateAsync(
            new CreateEggGradeCommand("Large", "Size", 0, true, -1));
        var update = await new UpdateEggGradeValidator().ValidateAsync(
            new UpdateEggGradeCommand(Guid.NewGuid(), "Large", 0, true, -1));

        Assert.Contains(create.Errors, e => e.ErrorCode == "EggGrade.LowStockFloor.Range");
        Assert.Contains(update.Errors, e => e.ErrorCode == "EggGrade.LowStockFloor.Range");
    }

    [Fact]
    public async Task Validators_accept_an_absent_floor()
    {
        // The ordinary grade has no floor, and every caller that predates #911
        // sends no such property at all.
        var create = await new CreateEggGradeValidator().ValidateAsync(
            new CreateEggGradeCommand("Large", "Size", 0, true));
        var update = await new UpdateEggGradeValidator().ValidateAsync(
            new UpdateEggGradeCommand(Guid.NewGuid(), "Large", 0, true));

        Assert.True(create.IsValid);
        Assert.True(update.IsValid);
    }

    [Fact]
    public void Below_the_floor_measures_available_alone()
    {
        // Restricted eggs are under a withdrawal period and cannot be sold, so
        // a grade with 1,200 restricted and 900 available is below a floor of
        // 1,000 — counting the restricted lot would hide a grade with nothing
        // left to sell.
        var restrictedHeavy = new StockByGrade(
            Guid.NewGuid(), "Small", 0, Available: 900, Restricted: 1200, LowStockFloor: 1000);

        Assert.True(restrictedHeavy.BelowFloor);
    }

    [Theory]
    [InlineData(1000, 999, true)]
    [InlineData(1000, 1000, false)]
    [InlineData(1000, 1001, false)]
    [InlineData(0, 0, false)]
    public void The_floor_is_a_minimum_not_a_threshold_to_exceed(int floor, int available, bool expected)
    {
        var row = new StockByGrade(
            Guid.NewGuid(), "Small", 0, available, Restricted: 0, LowStockFloor: floor);

        Assert.Equal(expected, row.BelowFloor);
    }

    [Fact]
    public void A_grade_with_no_floor_is_never_below_it()
    {
        var row = new StockByGrade(
            Guid.NewGuid(), "Medium", 0, Available: 0, Restricted: 0, LowStockFloor: null);

        Assert.False(row.BelowFloor);
    }
}
