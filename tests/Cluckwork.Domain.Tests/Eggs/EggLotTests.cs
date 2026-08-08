namespace Cluckwork.Domain.Tests.Eggs;

using Cluckwork.Domain.Eggs;

public sealed class EggLotTests
{
    private static EggLot MakeLot(int quantity = 100) =>
        EggLot.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            DateOnly.FromDateTime(DateTime.Today), Guid.NewGuid(), quantity);

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    [Fact]
    public void Allocate_WithinAvailable_Succeeds()
    {
        var lot = MakeLot(100);
        var result = lot.Allocate(30, Today);
        Assert.True(result.IsSuccess);
        Assert.Equal(70, lot.QuantityAvailable);
        Assert.Equal(1, lot.Version);
    }

    [Fact]
    public void Allocate_ExceedsAvailable_Fails()
    {
        var lot = MakeLot(50);
        var result = lot.Allocate(51, Today);
        Assert.True(result.IsFailure);
        Assert.Equal("EggLot.InsufficientStock", result.Error.Code);
    }

    [Fact]
    public void Allocate_UnderWithdrawal_Fails()
    {
        var lot = MakeLot(100);
        lot.SetWithdrawalRestriction(Today.AddDays(7));
        var result = lot.Allocate(10, Today);
        Assert.True(result.IsFailure);
        Assert.Equal("EggLot.WithdrawalRestricted", result.Error.Code);
    }

    [Fact]
    public void Allocate_AfterWithdrawalExpiry_Succeeds()
    {
        var lot = MakeLot(100);
        lot.SetWithdrawalRestriction(Today.AddDays(-1)); // expired yesterday
        var result = lot.Allocate(10, Today);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void ClearWithdrawal_AllowsAllocation()
    {
        var lot = MakeLot(100);
        lot.SetWithdrawalRestriction(Today.AddDays(7));
        lot.ClearWithdrawalRestriction();
        Assert.True(lot.Allocate(10, Today).IsSuccess);
    }

    [Fact]
    public void Restore_AfterAllocate_ReturnsQuantity_AndBumpsVersion()
    {
        var lot = MakeLot(100);
        Assert.True(lot.Allocate(40, Today).IsSuccess);
        var before = lot.Version;

        var result = lot.Restore(40);

        Assert.True(result.IsSuccess);
        Assert.Equal(100, lot.QuantityAvailable);
        Assert.Equal(before + 1, lot.Version);
    }

    [Fact]
    public void Restore_ExceedingProduced_Fails()
    {
        var lot = MakeLot(100);
        Assert.True(lot.Allocate(40, Today).IsSuccess);

        var result = lot.Restore(41);
        Assert.True(result.IsFailure);
        Assert.Equal("EggLot.RestoreExceedsProduced", result.Error.Code);
        Assert.Equal(60, lot.QuantityAvailable);
    }

    [Fact]
    public void Restore_NonPositive_Fails()
    {
        var lot = MakeLot(100);
        Assert.True(lot.Restore(0).IsFailure);
        Assert.True(lot.Restore(-5).IsFailure);
    }

    [Fact]
    public void Restore_IgnoresWithdrawalRestriction()
    {
        // Eggs return to the lot they came from even if it is now restricted;
        // the restriction then governs any future sale as usual.
        var lot = MakeLot(100);
        Assert.True(lot.Allocate(30, Today).IsSuccess);
        lot.SetWithdrawalRestriction(Today.AddDays(7));

        Assert.True(lot.Restore(30).IsSuccess);
        Assert.Equal(100, lot.QuantityAvailable);
    }

    // #69 — entry adjust/void reconciliation. The sold portion is untouchable;
    // available absorbs the whole delta.
    [Fact]
    public void AdjustProduction_GrowAndShrink_PreservesSold()
    {
        var lot = MakeLot(100);
        Assert.True(lot.Allocate(30, Today).IsSuccess); // sold 30, available 70

        Assert.True(lot.AdjustProduction(120).IsSuccess);
        Assert.Equal(120, lot.QuantityProduced);
        Assert.Equal(90, lot.QuantityAvailable);

        Assert.True(lot.AdjustProduction(40).IsSuccess);
        Assert.Equal(40, lot.QuantityProduced);
        Assert.Equal(10, lot.QuantityAvailable);
    }

    [Fact]
    public void AdjustProduction_BelowSold_Fails()
    {
        var lot = MakeLot(100);
        Assert.True(lot.Allocate(30, Today).IsSuccess);

        var result = lot.AdjustProduction(29);
        Assert.True(result.IsFailure);
        Assert.Equal("EggLot.SoldExceedsAdjusted", result.Error.Code);
        Assert.Equal(100, lot.QuantityProduced);
    }

    [Fact]
    public void AdjustProduction_ToZero_EmptiesUnsoldLot()
    {
        var lot = MakeLot(100);
        Assert.True(lot.AdjustProduction(0).IsSuccess);
        Assert.Equal(0, lot.QuantityProduced);
        Assert.Equal(0, lot.QuantityAvailable);
    }

    [Fact]
    public void AdjustProduction_Negative_Fails()
    {
        var lot = MakeLot(100);
        Assert.True(lot.AdjustProduction(-1).IsFailure);
    }

    // #406 — standalone stock write-off / reconciliation. Available moves,
    // production stays: the day's laying is a fact this method never restates.
    [Fact]
    public void AdjustAvailable_NegativeWithinAvailable_Succeeds_AndBumpsVersion()
    {
        var lot = MakeLot(100);
        var before = lot.Version;

        var result = lot.AdjustAvailable(-10);

        Assert.True(result.IsSuccess);
        Assert.Equal(90, lot.QuantityAvailable);
        Assert.Equal(100, lot.QuantityProduced);
        Assert.Equal(before + 1, lot.Version);
    }

    [Fact]
    public void AdjustAvailable_ToExactlyZero_Succeeds()
    {
        var lot = MakeLot(100);
        Assert.True(lot.AdjustAvailable(-100).IsSuccess);
        Assert.Equal(0, lot.QuantityAvailable);
    }

    [Fact]
    public void AdjustAvailable_BelowZero_Fails_AndChangesNothing()
    {
        var lot = MakeLot(100);
        var before = lot.Version;

        var result = lot.AdjustAvailable(-101);

        Assert.True(result.IsFailure);
        Assert.Equal("EggLot.InsufficientStock", result.Error.Code);
        Assert.Equal(100, lot.QuantityAvailable);
        Assert.Equal(before, lot.Version);
    }

    [Fact]
    public void AdjustAvailable_PositiveWithinProduced_Succeeds()
    {
        var lot = MakeLot(100);
        Assert.True(lot.AdjustAvailable(-30).IsSuccess); // wrote off 30
        Assert.True(lot.AdjustAvailable(5).IsSuccess);   // recount found 5
        Assert.Equal(75, lot.QuantityAvailable);
        Assert.Equal(100, lot.QuantityProduced);
    }

    [Fact]
    public void AdjustAvailable_PositiveToExactlyProduced_Succeeds()
    {
        var lot = MakeLot(100);
        Assert.True(lot.AdjustAvailable(-30).IsSuccess);
        Assert.True(lot.AdjustAvailable(30).IsSuccess);
        Assert.Equal(100, lot.QuantityAvailable);
    }

    [Fact]
    public void AdjustAvailable_PositiveBeyondProduced_Fails_AndChangesNothing()
    {
        var lot = MakeLot(100);
        Assert.True(lot.Allocate(30, Today).IsSuccess); // available 70
        var before = lot.Version;

        var result = lot.AdjustAvailable(31); // 70 + 31 > 100 produced

        Assert.True(result.IsFailure);
        Assert.Equal("EggLot.ReconcileExceedsProduced", result.Error.Code);
        Assert.Equal(70, lot.QuantityAvailable);
        Assert.Equal(before, lot.Version);
    }

    [Fact]
    public void AdjustAvailable_Zero_Fails()
    {
        var lot = MakeLot(100);
        var result = lot.AdjustAvailable(0);
        Assert.True(result.IsFailure);
        Assert.Equal("EggLot.InvalidQuantity", result.Error.Code);
    }

    [Fact]
    public void AdjustAvailable_IgnoresWithdrawalRestriction()
    {
        // Spoiled eggs under withdrawal still need to leave the count — the
        // restriction protects sales, and a write-off is the safe direction.
        var lot = MakeLot(100);
        lot.SetWithdrawalRestriction(Today.AddDays(7));

        Assert.True(lot.AdjustAvailable(-10).IsSuccess);
        Assert.Equal(90, lot.QuantityAvailable);
    }
}
