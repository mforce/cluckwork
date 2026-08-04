namespace Cluckwork.Application.Features.Reports;

// Read-only report aggregates (#91, spec §19). Every number is computed
// server-side in one place — pages/cards never re-derive or sum rows.
public interface IReportQueries
{
    // Production (spec §19.3 hen-day %): official entries only — Draft isn't
    // submitted yet, Voided vacated its day (#82).
    Task<ProductionReport> GetProductionAsync(DateOnly from, DateOnly to, CancellationToken ct = default);

    // Money summaries — the callers gate these behind AdminOnly.
    Task<SalesSummary> GetSalesAsync(DateOnly from, DateOnly to, CancellationToken ct = default);
    Task<ExpenseSummary> GetExpensesAsync(DateOnly from, DateOnly to, CancellationToken ct = default);
    Task<ProfitReport> GetProfitAsync(DateOnly from, DateOnly to, CancellationToken ct = default);
}

// #396 — `Sellable` is the HAND-GRADED remainder (total − cracked − dirty −
// discarded): the figure Daily Entry's grading counts down to, and what #394
// requires the grade lines to reconcile against exactly.
//
// `FromCounts` is the eggs that became stock WITHOUT being hand-graded — the
// Cracked and Dirty counters, but only where the entry resolved that condition
// to a grade (see DailyEntry.CrackedGradeId). Deliberately a SEPARATE figure
// rather than folded into Sellable: the two answer different questions ("how
// many did we grade" vs "how many can we sell"), they were only ever equal
// because conditions used to be losses, and merging them would silently move
// the number the capture screen is validated against.
public sealed record ProductionDay(
    DateOnly Date, int TotalEggs, int Cracked, int Dirty, int Discarded,
    int Sellable, int FromCounts, int Deaths, long HenDays, decimal? HenDayPct);

public sealed record GradeTotal(Guid EggGradeId, string Name, int Quantity);

public sealed record ProductionReport(
    IReadOnlyList<ProductionDay> Days,
    int TotalEggs, int TotalSellable, int TotalFromCounts, int TotalDeaths,
    long TotalHenDays, decimal? PeriodHenDayPct,
    IReadOnlyList<GradeTotal> GradeTotals);

// About the period's ORDERS (order date in range): revenue is their confirmed
// totals; paid is settled payments attached to THOSE orders whenever they were
// received — so outstanding = revenue − paid is the period's open AR.
public sealed record SalesSummary(
    int ConfirmedCount, long RevenueMinorUnits, long PaidMinorUnits,
    long OutstandingMinorUnits, int VoidedCount,
    string CurrencyCode, int CurrencyMinorUnit);

public sealed record ExpenseCategoryTotal(Guid ExpenseCategoryId, string Name, long TotalMinorUnits);

public sealed record ExpenseSummary(
    IReadOnlyList<ExpenseCategoryTotal> Categories, long GrandTotalMinorUnits,
    string CurrencyCode, int CurrencyMinorUnit);

// "Basic" deliberately: confirmed revenue − recorded expenses, no COGS or
// inventory valuation. Both operands shipped so the figure is auditable.
public sealed record ProfitReport(
    long RevenueMinorUnits, long ExpensesMinorUnits, long ProfitMinorUnits,
    string CurrencyCode, int CurrencyMinorUnit);
