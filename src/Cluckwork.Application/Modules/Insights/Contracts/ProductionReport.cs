namespace Cluckwork.Application.Modules.Insights.Contracts;

// `TotalRecordedHenDays` and `TotalRatedEggs` are `PeriodHenDayPct`'s exact
// denominator and numerator (#780), carried so the period figure is
// reproducible from the payload rather than being a number nobody can check.
// `TotalRecordedHenDays` equals `TotalHenDays` on a period every flock
// recorded, and the gap between them is exactly what is missing.
//
// `TotalLosses` and the two period rates follow the farm's own grade setting
// (#1297): Cracked and Dirty eggs count as sellable exactly where the entry
// resolved the condition to a grade (`TotalFromCounts`), so the losses total
// is the raw cracked/dirty/discarded counters MINUS `TotalFromCounts`, and
//
//   sellable % = (TotalSellable + TotalFromCounts) / TotalEggs × 100
//   loss %     = TotalLosses / TotalEggs × 100
//
// add up to 100. Null, not 0, for a period with no eggs — the way
// `PeriodHenDayPct` treats a period with no recorded exposure.
//
// `FlockTotals` — the per-flock breakdown (#1299): one row per flock that
// filed an official entry in the range, each computed by the same walk as
// the period totals, so its columns sum to them exactly. `FlockName` is
// resolved from the flock the row belongs to (Insights → FlockManagement R).
public sealed record ProductionReport(
    IReadOnlyList<ProductionDay> Days,
    int TotalEggs, int TotalSellable, int TotalFromCounts, int TotalLosses, int TotalDeaths,
    long TotalHenDays, long TotalRecordedHenDays, int TotalRatedEggs,
    decimal? PeriodHenDayPct, decimal? PeriodSellablePct, decimal? PeriodLossPct,
    IReadOnlyList<GradeTotal> GradeTotals,
    IReadOnlyList<FlockTotal> FlockTotals);
