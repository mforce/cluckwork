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
    [InlineData("JPY", "¥")]   // halfwidth yen, never the fullwidth U+FFE5
    public void Symbol_ComesFromTheFrameworksCldrData(string code, string expected) =>
        Assert.Equal(expected, CurrencyCatalog.Resolve(code).Symbol);

    [Theory]
    [InlineData("USD")]
    [InlineData("EUR")]
    [InlineData("GBP")]
    [InlineData("JPY")]
    [InlineData("KRW")]
    [InlineData("CNY")]
    [InlineData("MXN")]
    public void Symbol_IsWidthNormalized(string code)
    {
        // ICU builds disagree on the width of some symbols (¥ vs ￥, ₩ vs ￦),
        // and both forms are one character, so no tiebreak can separate them.
        // Without normalization the table differs between a dev machine and
        // the server — which is how this was found, as a CI-only failure.
        var symbol = CurrencyCatalog.Resolve(code).Symbol;

        Assert.True(symbol.IsNormalized(System.Text.NormalizationForm.FormKC),
            $"{code} resolved to a non-canonical symbol: {string.Join(" ", symbol.Select(c => $"U+{(int)c:X4}"))}");
    }

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
