namespace Cluckwork.Domain.Sales;

/// <summary>
/// The most a farm lets a ceiling-bound seller take off a line's list price
/// (#727), held in basis points so the comparison below is exact integer
/// arithmetic with no division to round — and so a farm that later wants 12.5%
/// needs no second migration. Basis points are a STORAGE choice: percent is
/// what crosses the wire and what a screen shows.
/// </summary>
/// <remarks>
/// A null <c>DiscountCeiling?</c> is the absence of a ceiling. Zero basis
/// points is a legal and DIFFERENT setting meaning "give nothing away";
/// collapsing the two is #719's own null-means-two-things trap.
/// </remarks>
public readonly record struct DiscountCeiling
{
    /// <summary>100%, the whole list price.</summary>
    public const int MaxBasisPoints = 10_000;

    private DiscountCeiling(int basisPoints) => BasisPoints = basisPoints;

    public int BasisPoints { get; }

    public decimal Percent => BasisPoints / 100m;

    /// <summary>
    /// Throws rather than returning a <see cref="Result"/>: a stored value
    /// outside 0–10 000 is an invariant violation, not an expected failure —
    /// both writers into the column range-check first.
    /// </summary>
    public static DiscountCeiling FromBasisPoints(int basisPoints)
    {
        if (basisPoints is < 0 or > MaxBasisPoints)
            throw new ArgumentOutOfRangeException(
                nameof(basisPoints), basisPoints,
                $"A discount ceiling must be between 0 and {MaxBasisPoints} basis points.");
        return new DiscountCeiling(basisPoints);
    }

    /// <summary>
    /// Parses the wire form — a percent — into basis points. A null input is a
    /// valid ABSENCE of a ceiling, not a failure.
    /// </summary>
    /// <remarks>
    /// This is the ONE parser, in the same spirit as
    /// <see cref="DiscountReason.TryParseCode"/>: UpdateFarmSettingsValidator
    /// turns a malformed percent into a 400 with it, and the handler converts
    /// with it, so the boundary and the storage cannot disagree about which
    /// percents are expressible. More than two decimal places is refused rather
    /// than rounded — 12.345% has no exact basis-point value, and rounding it
    /// would store a ceiling the farm never typed.
    /// </remarks>
    public static bool TryParsePercent(decimal? percent, out DiscountCeiling? ceiling)
    {
        ceiling = null;
        if (percent is not { } value) return true;
        if (value is < 0m or > 100m) return false;

        var basisPoints = value * 100m;
        if (basisPoints != decimal.Truncate(basisPoints)) return false;

        ceiling = FromBasisPoints((int)basisPoints);
        return true;
    }

    /// <summary>
    /// Whether a line sold at <paramref name="unitPriceMinorUnits"/> against a
    /// list price of <paramref name="listUnitPriceMinorUnits"/> is discounted
    /// past this ceiling.
    /// </summary>
    /// <remarks>
    /// Cross-multiplied from <c>(list − unit) / list &gt; bp / 10 000</c>, so
    /// nothing is ever divided and no rounding step can disagree with itself at
    /// the boundary. Two consequences worth not re-deriving:
    /// <list type="bullet">
    /// <item>The <c>&gt;</c> is STRICT, so exactly on the boundary is allowed:
    /// "maximum 10%" means at most 10%, so 10.00% off passes and 10.01% does
    /// not. This is the single place that decision lives.</item>
    /// <item>Both sides are taken in <see cref="Int128"/> — the subtraction
    /// included, which a <c>long</c> would wrap silently — so no farm's minor
    /// units can overflow the arithmetic.</item>
    /// </list>
    /// A non-negative line sold against a zero list price falls out as "not
    /// exceeded" with no special case: the right side is 0 and the left side is
    /// −unit × 10 000 ≤ 0.
    /// </remarks>
    public bool IsExceededBy(long listUnitPriceMinorUnits, long unitPriceMinorUnits) =>
        ((Int128)listUnitPriceMinorUnits - unitPriceMinorUnits) * MaxBasisPoints
            > (Int128)BasisPoints * listUnitPriceMinorUnits;
}
