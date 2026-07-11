namespace Cluckwork.Domain.Tests.Common;

using Cluckwork.Domain.Common;

public sealed class MoneyTests
{
    [Fact]
    public void Add_SameCurrency_ReturnsSum()
    {
        var a = new Money(1000, "USD", 2);
        var b = new Money(250, "USD", 2);
        var result = a.Add(b);
        Assert.Equal(1250, result.MinorUnits);
        Assert.Equal("USD", result.CurrencyCode);
    }

    [Fact]
    public void Add_DifferentCurrency_Throws()
    {
        var a = new Money(1000, "USD", 2);
        var b = new Money(500, "EUR", 2);
        Assert.Throws<InvalidOperationException>(() => a.Add(b));
    }

    [Fact]
    public void Subtract_SameCurrency_ReturnsDifference()
    {
        var a = new Money(1000, "USD", 2);
        var b = new Money(300, "USD", 2);
        Assert.Equal(700, a.Subtract(b).MinorUnits);
    }

    [Fact]
    public void Multiply_ReturnsScaledAmount()
    {
        var price = new Money(150, "USD", 2);
        Assert.Equal(750, price.Multiply(5).MinorUnits);
    }

    [Fact]
    public void ToDecimal_ConvertsMinorUnitsCorrectly()
    {
        var money = new Money(1099, "USD", 2);
        Assert.Equal(10.99m, money.ToDecimal());
    }

    [Fact]
    public void Zero_HasZeroMinorUnits()
    {
        var zero = Money.Zero("GBP");
        Assert.Equal(0, zero.MinorUnits);
        Assert.False(zero.IsNegative);
    }
}
