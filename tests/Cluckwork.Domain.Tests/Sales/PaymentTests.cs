namespace Cluckwork.Domain.Tests.Sales;

using Cluckwork.Domain.Sales;

public sealed class PaymentTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    private static Payment Make(long amount = 1000) =>
        Payment.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Today, amount, "USD", 2, PaymentMethod.Cash);

    [Fact]
    public void Create_TrimsOptionals_SnapshotsCurrency()
    {
        var p = Payment.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Today, 1000, "USD", 2, PaymentMethod.Check, "  chk 42  ", "   ");
        Assert.Equal("chk 42", p.ReferenceNumber);
        Assert.Null(p.Note);
        Assert.False(p.Voided);
        Assert.Equal(0, p.Version);
        Assert.Equal("USD", p.CurrencyCode);
        Assert.Equal(2, p.CurrencyMinorUnit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void Create_NonPositiveAmount_Throws(long amount)
    {
        Assert.Throws<ArgumentException>(() => Make(amount));
    }

    [Fact]
    public void Void_SetsReason_BumpsVersion_OnceOnly()
    {
        var p = Make();
        var result = p.Void("  double entry  ");
        Assert.True(result.IsSuccess);
        Assert.True(p.Voided);
        Assert.Equal("double entry", p.VoidReason);
        Assert.Equal(1, p.Version);

        var again = p.Void("again");
        Assert.Equal("Payment.AlreadyVoided", again.Error.Code);
        Assert.Equal(1, p.Version);
    }

    [Fact]
    public void Void_BlankReason_Fails()
    {
        var p = Make();
        var result = p.Void("   ");
        Assert.Equal("Payment.ReasonRequired", result.Error.Code);
        Assert.False(p.Voided);
    }
}
