namespace Cluckwork.Domain.Tests.Accounts;

using Cluckwork.Domain.Accounts;

// §4.6 currency derivation. The minor unit follows ISO 4217, NOT the locale's
// display habit — it decides how a stored integer amount is read back, so a
// wrong one misstates every amount on the farm by a factor of 100.
public sealed class CurrencyCatalogTests
{
    [Theory]
    [InlineData("USD", 2)]
    [InlineData("EUR", 2)]
    [InlineData("MXN", 2)]
    [InlineData("JPY", 0)]   // no minor unit
    [InlineData("KRW", 0)]
    [InlineData("ISK", 0)]
    [InlineData("KWD", 3)]   // three-digit minor unit
    [InlineData("BHD", 3)]
    public void MinorUnit_FollowsIso4217(string code, int expected) =>
        Assert.Equal(expected, CurrencyCatalog.Resolve(code).MinorUnit);

    [Theory]
    [InlineData("USD", "$")]
    [InlineData("EUR", "€")]
    [InlineData("GBP", "£")]
    [InlineData("JPY", "¥")]
    public void Symbol_ComesFromTheFrameworksCldrData(string code, string expected) =>
        Assert.Equal(expected, CurrencyCatalog.Resolve(code).Symbol);

    [Fact]
    public void UnknownCode_FallsBackToCodeAndTwoDigits()
    {
        var info = CurrencyCatalog.Resolve("ZZZ");

        Assert.Equal("ZZZ", info.Code);
        Assert.Equal("ZZZ", info.Symbol);
        Assert.Equal(CurrencyCatalog.DefaultMinorUnit, info.MinorUnit);
    }

    [Fact]
    public void Resolve_NormalizesCaseAndWhitespace()
    {
        var info = CurrencyCatalog.Resolve("  jpy ");

        Assert.Equal("JPY", info.Code);
        Assert.Equal(0, info.MinorUnit);
    }

    [Theory]
    [InlineData("USD", true)]
    [InlineData("usd", true)]
    [InlineData("ZZZ", true)]     // unlisted but well-formed — legal, takes the fallback
    [InlineData("US", false)]
    [InlineData("USDD", false)]
    [InlineData("US1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsWellFormedCode_ChecksShapeOnly(string? code, bool expected) =>
        Assert.Equal(expected, CurrencyCatalog.IsWellFormedCode(code));
}
