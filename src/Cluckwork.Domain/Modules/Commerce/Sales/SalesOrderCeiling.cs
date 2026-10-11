namespace Cluckwork.Domain.Modules.Commerce.Sales;

// #727 — where one line sits against the farm's discount ceiling.
public enum LineCeilingStatus
{
    /// <summary>Measured and within the ceiling, or nothing was discounted.</summary>
    Within,
    /// <summary>Measured and past the ceiling.</summary>
    Exceeds,
    /// <summary>No list price to measure against, and no recorded fact saying there was none.</summary>
    Unmeasurable,
}

/// <summary>
/// The offending line a ceiling refusal names (#727). The domain decides WHO
/// breaches and the caller composes the refusal, because the grade NAME lives
/// in a repository this assembly cannot reach — the same seam
/// <c>SaleAllocationPlan</c>'s ShortEggGradeId already uses for
/// insufficient stock.
/// </summary>
/// <param name="EggGradeId">The breaching line's grade.</param>
/// <param name="Status">Why the line breaches: over the ceiling, or unmeasurable.</param>
/// <param name="DiscountPercent">
/// Exact and unrounded, and null EXACTLY when <paramref name="Status"/> is
/// <see cref="LineCeilingStatus.Unmeasurable"/> — printing a percent for a line
/// whose list price is unknown would be a fabricated number.
/// </param>
public readonly record struct CeilingBreach(
    Guid EggGradeId,
    LineCeilingStatus Status,
    decimal? DiscountPercent);
