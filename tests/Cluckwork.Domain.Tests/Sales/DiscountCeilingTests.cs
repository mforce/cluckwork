namespace Cluckwork.Domain.Tests.Sales;

using System.Globalization;
using Cluckwork.Domain.Sales;

// #727 — the shared vector set. These exact numbers are the table the SPA's own
// TypeScript mirror is held to, so the two implementations cannot disagree
// about the boundary; change a row here and the mirror changes with it.
public sealed class DiscountCeilingTests
{
    private static DiscountCeiling TenPercent() => DiscountCeiling.FromBasisPoints(1_000);

    // --- the boundary ------------------------------------------------------

    // list, unit, exceeded — a 10% ceiling.
    [Theory]
    [InlineData(1_000L, 900L, false)]   // exactly 10.00% off — ALLOWED
    [InlineData(1_000L, 899L, true)]    // 10.10% off
    [InlineData(10_000L, 9_000L, false)] // exactly 10.00% off at a finer scale
    [InlineData(10_000L, 8_999L, true)]  // 10.01% off — one minor unit past the boundary
    [InlineData(1_000L, 1_000L, false)]  // at list
    [InlineData(1_000L, 1_200L, false)]  // above list
    public void IsExceededBy_AllowsExactlyTheCeiling_AndRefusesOneMinorUnitPastIt(
        long list, long unit, bool exceeded) =>
        Assert.Equal(exceeded, TenPercent().IsExceededBy(list, unit));

    // A zero list price has nothing to discount from, and that falls out of the
    // cross-multiplication rather than needing a divide-by-zero guard.
    [Theory]
    [InlineData(0L)]
    [InlineData(500L)]
    public void IsExceededBy_WithAZeroListPrice_NeverBreaches(long unit)
    {
        Assert.False(TenPercent().IsExceededBy(0L, unit));
        Assert.False(DiscountCeiling.FromBasisPoints(0).IsExceededBy(0L, unit));
    }

    // Zero basis points is a legal setting, and a DIFFERENT one from no ceiling
    // at all: give nothing away.
    [Theory]
    [InlineData(1_000L, false)] // at list
    [InlineData(1_001L, false)] // above list
    [InlineData(999L, true)]    // one minor unit off list
    public void AZeroCeiling_RefusesAnyDiscountAtAll(long unit, bool exceeded) =>
        Assert.Equal(exceeded, DiscountCeiling.FromBasisPoints(0).IsExceededBy(1_000L, unit));

    // 100% off is exactly at a 100% ceiling, so the strict > allows it.
    [Theory]
    [InlineData(0L, false)]
    [InlineData(1L, false)]
    public void AHundredPercentCeiling_AllowsGivingTheWholeListPriceAway(long unit, bool exceeded) =>
        Assert.Equal(
            exceeded,
            DiscountCeiling.FromBasisPoints(DiscountCeiling.MaxBasisPoints).IsExceededBy(1_000L, unit));

    // --- Int128 headroom ---------------------------------------------------

    // Both products are ~9e21 here, a thousand times past long.MaxValue, and
    // they differ by 10 000 out of 9e21 — so a long multiply would wrap and a
    // double would call them equal. Only exact wide arithmetic separates these
    // two rows.
    [Theory]
    [InlineData(8_100_000_000_000_000_000L, false)] // exactly 10.00% off
    [InlineData(8_099_999_999_999_999_999L, true)]  // one minor unit past it
    public void IsExceededBy_StaysExactAtTheFullWidthOfALongListPrice(long unit, bool exceeded) =>
        Assert.Equal(exceeded, TenPercent().IsExceededBy(9_000_000_000_000_000_000L, unit));

    // The subtraction is taken in Int128 too. In long, MaxValue - MinValue
    // wraps to -1, which would report the largest discount expressible as no
    // discount at all. No writer can produce a negative unit price — the
    // AddOrderItem validator refuses one — so this pins the cast, not a
    // reachable sale.
    [Fact]
    public void IsExceededBy_DoesNotWrapWhenTheDiscountItselfExceedsALong() =>
        Assert.True(DiscountCeiling.FromBasisPoints(0).IsExceededBy(long.MaxValue, long.MinValue));

    // --- the percent wire form ---------------------------------------------

    // The percents travel as strings, not doubles: an InlineData double would
    // reach the parser through a binary-float conversion, and the exactness of
    // the two-decimal rule is the thing under test.
    [Theory]
    [InlineData("0", 0)]
    [InlineData("0.00", 0)]
    [InlineData("10", 1_000)]
    [InlineData("10.00", 1_000)]
    [InlineData("12.5", 1_250)]
    [InlineData("12.34", 1_234)]
    [InlineData("0.01", 1)]
    [InlineData("100", 10_000)]
    public void TryParsePercent_AcceptsUpToTwoDecimalPlaces(string percent, int expectedBasisPoints)
    {
        Assert.True(DiscountCeiling.TryParsePercent(Percent(percent), out var ceiling));
        Assert.Equal(expectedBasisPoints, ceiling!.Value.BasisPoints);
    }

    // A null percent is the ABSENCE of a ceiling, which is a valid input, not a
    // parse failure — the distinction the whole slice rests on.
    [Fact]
    public void TryParsePercent_TreatsNullAsAValidAbsence()
    {
        Assert.True(DiscountCeiling.TryParsePercent(null, out var ceiling));
        Assert.Null(ceiling);
    }

    // A third decimal place has no exact basis-point value, so it is refused
    // rather than rounded into a ceiling the farm never typed.
    [Theory]
    [InlineData("12.345")]
    [InlineData("0.001")]
    [InlineData("99.999")]
    [InlineData("-0.01")]
    [InlineData("-1")]
    [InlineData("100.01")]
    [InlineData("101")]
    public void TryParsePercent_RefusesAnythingItCannotStoreExactly(string percent)
    {
        Assert.False(DiscountCeiling.TryParsePercent(Percent(percent), out var ceiling));
        Assert.Null(ceiling);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(1_000, "10")]
    [InlineData(1_250, "12.5")]
    [InlineData(10_000, "100")]
    public void Percent_IsTheBasisPointsOverAHundred(int basisPoints, string expected) =>
        Assert.Equal(Percent(expected), DiscountCeiling.FromBasisPoints(basisPoints).Percent);

    [Theory]
    [InlineData(-1)]
    [InlineData(10_001)]
    public void FromBasisPoints_ThrowsOutsideTheRange(int basisPoints) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => DiscountCeiling.FromBasisPoints(basisPoints));

    private static decimal Percent(string literal) =>
        decimal.Parse(literal, CultureInfo.InvariantCulture);
}
