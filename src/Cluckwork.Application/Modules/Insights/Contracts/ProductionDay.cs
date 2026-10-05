namespace Cluckwork.Application.Modules.Insights.Contracts;

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
//   archived. It and `RecordedFlocks` can legitimately disagree EITHER WAY at a
//   lifecycle boundary — a flock files on a day the ledger says it had ended, or
//   a placement date is corrected forward past entries that already exist — so
//   never assume one bounds the other.
// `MissingFlocks` — expected flocks with no filing, and the ONLY sound test for
//   a partly recorded day. Comparing the two counts above is not: they are
//   counts over different sets, so expected {A, B} against filings {A, C} gives
//   2 and 2 and hides B entirely, presenting a day short one flock's eggs as
//   complete and feeding it into the Dashboard's Peak and Avg. `MissingFlocks`
//   is computed from identities. `ExpectedFlocks - MissingFlocks` is how many
//   of the flocks that owed a count actually filed one.
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
    int RecordedFlocks, int ExpectedFlocks, int MissingFlocks,
    long HenDays, long RecordedHenDays, int RatedEggs, decimal? HenDayPct);
