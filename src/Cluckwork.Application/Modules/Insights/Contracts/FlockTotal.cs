namespace Cluckwork.Application.Modules.Insights.Contracts;

// One row per flock in the period's per-flock breakdown (#1299). Carries the
// same #780 fields the per-day rows carry — `RecordedHenDays`, `RatedEggs` and
// `HenDayPct` — computed by the SAME walk as the period totals, so the rows
// sum to them exactly and the per-flock rate follows the #780 rules verbatim:
// recorded hen-days only, a numerator limited to that flock, and null — never
// 0 — for a flock whose filings all sit outside its lifecycle window (its
// eggs stay in `Eggs` and out of the rate, the #780 numerator rule).
//
// A row exists for every flock that filed at least one OFFICIAL entry in the
// range (the rule that keeps the rows reconcilable: eggs, sellable, losses,
// deaths, recorded hen-days and rated eggs come only from filings). A flock
// that owed a filing but never filed one appears in no column here — the
// daily table's Missing column is the record of that. `HouseId` is the house
// the flock belongs to; houses have no aggregate yet (phantom ids, #780), so
// this is the id, not a name. `PlacedOn` is the flock's placement date —
// flock names are NOT unique, so the row carries a second human-readable
// detail from the same batched query to keep two same-named rows apart.
public sealed record FlockTotal(
    Guid FlockId, string FlockName, DateOnly PlacedOn, Guid HouseId,
    int Eggs, int Sellable, int Losses, int Deaths,
    long RecordedHenDays, int RatedEggs, decimal? HenDayPct);
