namespace Cluckwork.Application.Modules.Insights.Contracts;

// `TotalRecordedHenDays` and `TotalRatedEggs` are `PeriodHenDayPct`'s exact
// denominator and numerator (#780), carried so the period figure is
// reproducible from the payload rather than being a number nobody can check.
// `TotalRecordedHenDays` equals `TotalHenDays` on a period every flock
// recorded, and the gap between them is exactly what is missing.
[ModuleContract("Insights")]
public sealed record ProductionReport(
    IReadOnlyList<ProductionDay> Days,
    int TotalEggs, int TotalSellable, int TotalFromCounts, int TotalDeaths,
    long TotalHenDays, long TotalRecordedHenDays, int TotalRatedEggs,
    decimal? PeriodHenDayPct,
    IReadOnlyList<GradeTotal> GradeTotals);
