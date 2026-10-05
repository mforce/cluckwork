namespace Cluckwork.Domain.Modules.Commerce.Contracts;

// #720 — why a line's ListUnitPriceMinorUnits is what it is. NULL alone cannot
// say, and #727 gates an approval on the difference: for ProductUnpriced and
// NotComparable, "no comparable list price" is a RECORDED FACT and no discount
// is computable; for PreDating it means "we do not know", and the line may have
// been deeply discounted. Those two need opposite treatment.
//
// PreDating is written by the backfill only. Nothing in the application ever
// sets it — a row the code writes always knows its own basis.
public enum ListPriceBasis
{
    /// <summary>A comparable list price was captured; ListUnitPriceMinorUnits is non-null.</summary>
    Recorded,
    /// <summary>The product had no default price at all.</summary>
    ProductUnpriced,
    /// <summary>The product's currency code or minor unit did not match the order's.</summary>
    NotComparable,
    /// <summary>The row predates the column. Backfill only — never written by the application.</summary>
    PreDating,
}
