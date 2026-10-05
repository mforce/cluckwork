using Cluckwork.Application.Modules.Insights.Contracts;

namespace Cluckwork.Application.Modules.Insights.Reports;

// Read-only report aggregates (#91, spec §19). Every number is computed
// server-side in one place — pages/cards never re-derive or sum rows.
public interface IReportQueries
{
    // Production (spec §19.3 hen-day %): official entries only — Draft isn't
    // submitted yet, Voided vacated its day (#82).
    //
    // `flockId` narrows every figure to one flock, INCLUDING the hen-day
    // exposure, so the rate's numerator and denominator stay the same flock's
    // (#916). It is a query parameter, not a post-filter: a caller cannot
    // reconstruct a scoped `HenDayPct` from the farm-wide payload, because the
    // rate is computed per day from a denominator the payload only carries
    // summed over every flock. null = farm-wide, byte-for-byte as before.
    // Visibility is the ENDPOINT's job — this is a report query, not an
    // authorization boundary.
    Task<ProductionReport> GetProductionAsync(
        DateOnly from, DateOnly to, Guid? flockId = null, CancellationToken ct = default);

    // Money summaries — the callers gate these behind AdminOnly.
    Task<SalesSummary> GetSalesAsync(DateOnly from, DateOnly to, CancellationToken ct = default);
    Task<ExpenseSummary> GetExpensesAsync(DateOnly from, DateOnly to, CancellationToken ct = default);
    Task<ProfitReport> GetProfitAsync(DateOnly from, DateOnly to, CancellationToken ct = default);
}

