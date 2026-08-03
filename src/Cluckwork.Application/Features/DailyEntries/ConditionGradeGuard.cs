namespace Cluckwork.Application.Features.DailyEntries;

using Cluckwork.Application.Features.EggGrades;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Eggs;

// #396 — a condition grade is fed by its Daily Entry COUNTER and never by a
// manual grade line.
//
// Hiding those grades from the Grading pane is a UI affordance, not the
// enforcement. The eligibility check a manual line passes is "active and
// saleable" with NO kind restriction, and this feature makes Cracked and Dirty
// saleable — so a direct or stale API client can name a Cracked id as a manual
// line. The exact-total check (#394) does not catch it, because that check is
// only a sum and the sum still reconciles. Submission would then create the
// manual lot AND the counter-backed quality lot for the same grade: two lots for
// one grade on one day, double-counting the day's stock and breaking the
// one-lot-per-grade assumption lot reconciliation depends on.
//
// Lives in the handlers rather than in a validator on purpose (#394): the demo
// and simulation seeders construct commands and call HandleAsync directly,
// skipping validators entirely, so a validator-only rule would let a fixture
// build exactly the state this refuses.
internal static class ConditionGradeGuard
{
    internal const string ErrorCode = "DailyEntry.ConditionGradeNotManual";

    // Reads the WHOLE catalog (ListAllAsync) rather than the active+saleable
    // slice the callers already fetch, because the question here is "is this id
    // bound to a counter", which does not depend on the grade's current flags.
    //
    // Stated at the strength the evidence supports: this is DEFENSIVE, not a
    // fix for a reachable bug. Today an inactive grade is already refused
    // upstream by the UnknownGrade check, so narrowing this to ListActiveAsync
    // would change the error code a caller sees, not whether the write is
    // refused — no test distinguishes them and none is claimed to. It is written
    // this way so the guard keeps answering the same question if either
    // upstream check is later relaxed, which is exactly the kind of coupling
    // that turns a refusal into an acceptance without anything going red.
    internal static async Task<Error?> CheckAsync(
        IEggGradeRepository eggGrades,
        IEnumerable<Guid> lineGradeIds,
        CancellationToken ct)
    {
        var ids = lineGradeIds as IReadOnlyCollection<Guid> ?? lineGradeIds.ToList();
        if (ids.Count == 0) return null;

        var conditionGradeIds = (await eggGrades.ListAllAsync(ct))
            .Where(g => g.DailyEntryKind != DailyEntryKind.Manual)
            .Select(g => g.Id)
            .ToHashSet();

        return ids.Any(conditionGradeIds.Contains)
            ? Error.Validation(
                ErrorCode,
                "Cracked and Dirty eggs are captured by their own counters, not as graded lines. "
                + "Remove the condition grade from the grading breakdown and use the Cracked or "
                + "Dirty count instead.")
            : null;
    }
}
