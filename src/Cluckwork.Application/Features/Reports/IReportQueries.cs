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
// #780 — three fields exist so a consumer can tell what the farm KNOWS from
// what the farm PRODUCED. Every other figure here is 0 both for a day nobody
// recorded and for a day that genuinely produced no eggs, so without them the
// two are indistinguishable, which is what left the Dashboard's 14-day strip
// asserting a zero it had no evidence for.
//
// `RecordedFlocks` — flocks that filed an OFFICIAL entry (Submitted, Locked,
//   ManagerAdjusted). 0 means nobody recorded the day; it does not mean the
//   farm produced nothing. Counted from the entries, so a flock that filed
//   counts whether or not the bird ledger agrees it was live that day.
// `ExpectedFlocks` — flocks that owed one: placed, not yet depleted or
//   archived. `RecordedFlocks < ExpectedFlocks` is a PARTIALLY recorded day,
//   whose egg total is a floor rather than a figure. The two can legitimately
//   disagree EITHER WAY at a lifecycle boundary — a flock files on a day the
//   ledger says it had ended, or a placement date is corrected forward past
//   entries that already exist — so never assume one bounds the other.
// `RecordedHenDays` — the exposure behind the rate. `HenDays` keeps the
//   glossary's meaning (one bird alive for one day, over every flock) so the
//   Reports column still says what it always said; the rate divides by the
//   subset with evidence behind it. Dividing by `HenDays` is what made an
//   unrecorded day read as a day of zero production, and did the same to a day
//   one flock of three missed.
// `RatedEggs` — `HenDayPct`'s NUMERATOR: the eggs of exactly the flocks whose
//   birds are in `RecordedHenDays`. Equal to `TotalEggs` on any day whose
//   filings all come from live flocks with birds, which is every ordinary day.
//   It is carried rather than left implicit so the rate is reproducible from
//   the payload; without it, `TotalEggs / RecordedHenDays` silently disagrees
//   with the percentage beside it on exactly the contradictory days.
public sealed record ProductionDay(
    DateOnly Date, int TotalEggs, int Cracked, int Dirty, int Discarded,
    int Sellable, int FromCounts, int Deaths,
    int RecordedFlocks, int ExpectedFlocks,
    long HenDays, long RecordedHenDays, int RatedEggs, decimal? HenDayPct);

public sealed record GradeTotal(Guid EggGradeId, string Name, int Quantity);

// `TotalRecordedHenDays` and `TotalRatedEggs` are `PeriodHenDayPct`'s exact
// denominator and numerator (#780), carried so the period figure is
// reproducible from the payload rather than being a number nobody can check.
// `TotalRecordedHenDays` equals `TotalHenDays` on a period every flock
// recorded, and the gap between them is exactly what is missing.
public sealed record ProductionReport(
    IReadOnlyList<ProductionDay> Days,
    int TotalEggs, int TotalSellable, int TotalFromCounts, int TotalDeaths,
    long TotalHenDays, long TotalRecordedHenDays, int TotalRatedEggs,
    decimal? PeriodHenDayPct,
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
