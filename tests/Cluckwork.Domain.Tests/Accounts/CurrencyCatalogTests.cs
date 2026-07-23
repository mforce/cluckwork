namespace Cluckwork.Domain.Tests.Accounts;

using Cluckwork.Domain.Accounts;

// §4.6 currency derivation. The minor unit follows ISO 4217, NOT the locale's
// display habit — it decides how a stored integer amount is read back, so a
// wrong one misstates every amount on the farm by a factor of 100.
public sealed class CurrencyCatalogTests
{
    // EVERY non-2 currency, not a sample: a spot-check leaves the rest of the
    // table free to be deleted with the suite still green, and each missing
    // entry silently reads its farm's stored amounts 100x or 1000x out
    // (codex review of #159). Values are ISO 4217, not copied from the
    // implementation's table.
    [Theory]
    // zero-decimal
    [InlineData("BIF", 0)]
    [InlineData("CLP", 0)]
    [InlineData("DJF", 0)]
    [InlineData("GNF", 0)]
    [InlineData("ISK", 0)]
    [InlineData("JPY", 0)]
    [InlineData("KMF", 0)]
    [InlineData("KRW", 0)]
    [InlineData("PYG", 0)]
    [InlineData("RWF", 0)]
    [InlineData("UGX", 0)]
    [InlineData("UYI", 0)]
    [InlineData("VND", 0)]
    [InlineData("VUV", 0)]
    [InlineData("XAF", 0)]
    [InlineData("XOF", 0)]
    [InlineData("XPF", 0)]
    // three-decimal
    [InlineData("BHD", 3)]
    [InlineData("IQD", 3)]
    [InlineData("JOD", 3)]
    [InlineData("KWD", 3)]
    [InlineData("LYD", 3)]
    [InlineData("OMR", 3)]
    [InlineData("TND", 3)]
    // four-decimal
    [InlineData("CLF", 4)]
    [InlineData("UYW", 4)]
    // the ordinary two-decimal majority
    [InlineData("USD", 2)]
    [InlineData("EUR", 2)]
    [InlineData("MXN", 2)]
    [InlineData("GBP", 2)]
    [InlineData("INR", 2)]
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

    // ICU wraps several symbols in bidi marks and hands back at least one that
    // IS nothing but an invisible: CVE arrives as a bare U+200B ZERO WIDTH
    // SPACE, and the Gulf currencies carry a trailing U+200F RIGHT-TO-LEFT
    // MARK. Category Cf is not whitespace to .NET, so a blank check passes it,
    // and being one character it WINS the shortest-symbol rule — the farm would
    // render every amount with no visible currency marker, or flip text
    // direction mid-sentence (adversarial review of #159).
    [Theory]
    [InlineData("CVE")]
    [InlineData("BHD")]
    [InlineData("EGP")]
    [InlineData("JOD")]
    [InlineData("KWD")]
    [InlineData("LBP")]
    [InlineData("LYD")]
    [InlineData("OMR")]
    [InlineData("QAR")]
    [InlineData("SAR")]
    [InlineData("YER")]
    [InlineData("USD")]
    [InlineData("INR")]
    public void Symbol_IsAlwaysSomethingYouCanSee(string code)
    {
        var symbol = CurrencyCatalog.Resolve(code).Symbol;

        Assert.NotEqual(0, symbol.Length);
        Assert.DoesNotContain(symbol, char.IsControl);
        Assert.DoesNotContain(symbol, c =>
            System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                == System.Globalization.UnicodeCategory.Format);
    }

    [Theory]
    [InlineData("USD")]
    [InlineData("KWD")]
    [InlineData("CVE")]
    [InlineData("INR")]
    [InlineData("VND")]
    public void Symbol_FitsTheStoredColumn(string code) =>
        // Otherwise the save fails on a string-truncation error at the database
        // rather than here, and only for whoever picked that currency.
        Assert.True(CurrencyCatalog.Resolve(code).Symbol.Length <= CurrencyCatalog.MaxSymbolLength);

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
