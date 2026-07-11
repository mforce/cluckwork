namespace Cluckwork.Domain.Tests.Eggs;

using Cluckwork.Domain.Eggs;

public sealed class EggLotTests
{
    private static EggLot MakeLot(int quantity = 100) =>
        EggLot.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            DateOnly.FromDateTime(DateTime.Today), "A-Large", quantity);

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
}
