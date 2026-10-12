using Cluckwork.Infrastructure.Jobs;

namespace Cluckwork.Infrastructure.Persistence;

public sealed partial class SimulationDataSeeder
{
    // --- Completion manifest (#243 Task 3e) -----------------------------
    //
    // The final seeding step: COUNT what was actually created, VALIDATE
    // those counts against what the configured SimulationOptions intended to
    // create, and (only when CredentialOutputPath is set) WRITE a JSON
    // manifest recording the result. This is what closes the "partial seed
    // silently looks done" gap — the #243 findings header and #277's
    // Playwright suite both read this file and trust `complete: true` outright;
    // a short/partial seed must throw HERE instead of quietly producing a
    // manifest that says otherwise. (SeedAsync itself returns a SeedResult, not
    // the manifest — #279; the manifest is the on-disk artifact, and tests
    // point Simulation:CredentialOutputPath at a temp file to read it back.)
    //
    // Depth-robust by construction: every expectation in ComputeExpectedCounts
    // is derived from SimulationOptions (sim.Managers/.../HistoryDays), the
    // seeder's own "today"/farm-local-"today" anchors, or this class's own
    // structural constants (RecurringStartDay/RecurringCadenceDays,
    // DraftWindowDays, GradesPerDailyEntry, FeedUsageDays/WaterUsageDays, the
    // hardcoded topology/catalog/expense array lengths) — nothing here
    // hardcodes "90 days" or "12 days"; the shallow SimulationSeedFactory test
    // fixture (HistoryDays=12) and the real 90-day default validate against
    // the SAME formulas.
    //
    // #638 — that claim covers the EXPECTATIONS, and until #638 the SEEDING
    // was not depth-robust to match: the lock sweep ran once, capped at
    // DailyEntryLockSweep.BatchSize, so above HistoryDays 107 the Locked
    // expectation below could not be met however clean the database. The
    // seeding side is DrainLockSweepAsync's job; read the two together before
    // widening either claim.
    //
    // #279 review Fix 2 (Agent A + pi + codex, consensus): every count below
    // is asserted EXACTLY, not as a ">= 1" floor — including the
    // Draft/Submitted/Locked lifecycle split (Locked is derived by mirroring
    // DailyEntryLockSweep's own cutoff — see ExpectedLockedEntryCount below —
    // rather than left unchecked; Draft/Submitted then reconcile against the
    // total). A bug that drops all-but-one row of any seeder-controlled band
    // now fails ValidateCounts instead of still certifying complete: true.

    // Bumped to 2 by #627: the persisted manifest GAINED Customers/BirdMovements
    // count fields and a Flocks lifecycle split — a schema-1 reader must
    // detect the reshape instead of silently misparsing it.
    public const int ManifestSchemaVersion = 2;

    // Mirrors SeedFlockTopologyAsync's `wanted` array length. Kept as an
    // independent constant (not read off that array) so ComputeExpectedCounts
    // needs no DB round-trip to state what the seed intends to produce — it
    // still takes `today`/`farmToday` as plain DateOnly inputs (#279 Fix 2,
    // for the exact Locked/Submitted split below) but is otherwise pure.
    private const int FlockTopologyCount = 2;

    // #627 — mirrors the picker catalog SeedFlockCatalogAsync creates:
    // NumberedCatalogFlockCount is what the numbered loop iterates; the other
    // three catalog rows (the page-two sentinel, the Depleted, the Archived)
    // are fixed-name rows outside the loop. CatalogFlockBandCount is the
    // 100-row catalog total the EXPECTED counts read — kept as an independent
    // constant for the same reason FlockTopologyCount is: ComputeExpectedCounts
    // states the seed's intent without a DB round-trip.
    private const int NumberedCatalogFlockCount = 97;
    private const int CatalogFlockBandCount = 100;

    // #627 — the customer band SeedCustomersAsync creates on top of the three
    // lifecycle customers in the Sales partial (97 zero-padded fillers + the page-two
    // sentinel). Total customers is CustomersWanted.Length + this.
    private const int CustomerBandCount = CustomerFillerCount + 1;

    // #627 — mirrors SeedExplicitBirdMovementsAsync's explicit row count.
    private const int ExplicitBirdMovementsCount = 51;

    // #627 — mirrors SeedFeedAdjustmentsAsync' added adjustment rows on the
    // feed lot (its ten pre-existing movements + this = exactly 120 on the
    // feed item; the bedding item keeps its two, so account-wide is 122).
    private const int FeedAdjustmentBandCount = 110;

    // #627 — the automatic mortality baseline, derived the SAME way
    // SeedFlockHistoryAsync generates it: one row per (operational flock, day)
    // where d % 5 == 0 for d in DraftWindowDays+1..historyDays — only days
    // that are actually SUBMITTED mint a mortality row. An explicit loop, not
    // division: the Draft window is part of the rule, and the expected count
    // must stay correct if it changes. (90-day history: 18 per flock, 36
    // account-wide; 12-day: 2 per flock.)
    private static int AutomaticMortalityPerFlock(int historyDays)
    {
        var count = 0;
        for (var d = DraftWindowDays + 1; d <= historyDays; d++)
            if (d % 5 == 0) count++;
        return count;
    }

    // Mirrors SeedFlockHistoryAsync's per-entry grade-quantity array literal
    // (Large/Medium/Small): SubmitDailyEntryHandler mints exactly one egg lot
    // per grade line, and the deterministic baseline/cracked/dirty/discarded
    // math in SeedFlockHistoryAsync always leaves all three grade buckets > 0
    // for every configured baseline — so every SUBMITTED entry (never a
    // still-Draft one; see DraftWindowDays) produces exactly this many lots,
    // not merely "at least one" (#279 Fix 2).
    private const int GradesPerDailyEntry = 3;

    // #396: submit ALSO mints a lot for each condition counter that resolved
    // to a condition grade (DailyEntryKind.Cracked/Dirty) and carried a
    // positive count. Kept separate from GradesPerDailyEntry rather than
    // folded into it: these are NOT grade lines — no GradeQuantityDto names
    // them, and ConditionGradeGuard actively refuses one that does — so the
    // two constants answer different questions and drift for different
    // reasons. Both counters are structurally positive here (`cracked = 4 +
    // d % 3` >= 4, `dirty = 2 + d % 2` >= 2, for every day and baseline), and
    // the base seed ships Cracked/Dirty active + saleable, so both resolve on
    // every submitted entry — exactly 2, never "up to 2". If a future fixture
    // lets either counter reach 0, SubmitDailyEntryHandler skips that lot and
    // this must become a per-entry sum instead of a constant.
    private const int ConditionLotsPerDailyEntry = 2;

    // Mirrors SeedSalesAsync's two EnsureDraftOrderAsync calls (customer1/2,
    // never confirmed) and its three lifecycle EnsureConfirmedOrderAsync
    // calls (customer1/2 unpaid + customer3 partially paid) — the recurring
    // drip (SeedRecurringOrdersAsync) is added on top via RecurringPointCount.
    private const int DraftOrdersCount = 2;
    private const int LifecycleConfirmedOrdersCount = 3;

    // Mirrors SeedExpensesAsync's `wanted` array length (the 4 hardcoded
    // fixture expenses) — the recurring drip (SeedRecurringExpensesAsync) is
    // added on top via RecurringPointCount, same convention as orders above.
    private const int LifecycleExpensesCount = 4;

    // SeedRecurringOrdersAsync and SeedRecurringExpensesAsync both loop
    // `for (var d = RecurringStartDay; d <= historyDays; d += RecurringCadenceDays)`
    // — this is that loop's iteration count, computed once so the manifest's
    // expectations never drift from what those two loops actually do.
    private static int RecurringPointCount(int historyDays) =>
        historyDays < RecurringStartDay ? 0 : (historyDays - RecurringStartDay) / RecurringCadenceDays + 1;

    // #638 — DRAIN the sweep; one pass is not enough at depth.
    //
    // DailyEntryLockSweep is built for the DurableJobWorker: one RunAsync pass
    // locks at most DailyEntryLockSweep.BatchSize entries per account and
    // returns, and the poll 30 seconds later takes the next batch. A
    // run-then-exit seed verb has no poll behind it, so a single pass left
    // every eligible entry past the first batch Submitted — while
    // ExpectedLockedEntryCount (below) mirrors the sweep's CUTOFF rule and
    // therefore expects every eligible entry Locked. Those two agree only
    // while `FlockTopologyCount * (HistoryDays - LockAfterDays) <= BatchSize`,
    // i.e. HistoryDays <= 107; above it the seed could never validate on any
    // database, however clean.
    //
    // Fixed here rather than by raising BatchSize: that constant is sized for
    // the serving-path background worker, and enlarging it to suit a seeder
    // would move this ceiling rather than remove it.
    //
    // Terminates on a pass that locked nothing — the honest "no progress"
    // signal (see DailyEntryLockSweep.RunAsync's own contract), which also
    // covers the stuck-batch case a due-count terminator would spin on. The
    // bound exists only so a future defect that makes a pass report progress
    // without making any cannot hang a seed forever; it is derived from the
    // same numbers the work itself is, so it can never be the thing that
    // fails first on a legitimately deep fixture.
    private async Task DrainLockSweepAsync(SimulationOptions sim, CancellationToken ct)
    {
        var maxPasses = MaxLockSweepPasses(sim.HistoryDays);
        for (var pass = 1; pass <= maxPasses; pass++)
            if (await lockSweep.RunAsync(ct) == 0)
                return;

        throw new InvalidOperationException(
            $"Daily-entry lock sweep did not drain in {maxPasses} passes at "
            + $"HistoryDays={sim.HistoryDays}; every pass still reported entries locked. "
            + "This is a lock-sweep defect, not a seeding depth limit.");
    }

    // Upper bound on the passes DrainLockSweepAsync should ever need: enough
    // to lock every seeded daily entry across the topology, plus the
    // terminating pass that locks nothing, plus one of slack.
    //
    // Deliberately loose in two directions, because the bound must never be
    // the thing that fails first on a legitimate fixture. It counts EVERY
    // seeded entry as a candidate while only those past the lock cutoff are
    // ever due; and it divides by one BatchSize per pass while a pass sweeps
    // every account, so the real per-pass ceiling is higher still. It exists
    // solely so a future defect that reports progress without making any
    // cannot hang a seed forever.
    private static int MaxLockSweepPasses(int historyDays) =>
        (FlockTopologyCount * Math.Max(historyDays, 0) + DailyEntryLockSweep.BatchSize - 1)
            / DailyEntryLockSweep.BatchSize
        + 2;

    // Mirrors DailyEntryLockSweep.LockDueEntriesAsync's own cutoff
    // (`farmToday.AddDays(-LockAfterDays)`) entry-by-entry, so the manifest's
    // expected Locked count is a real derivation from the SAME rule the
    // sweep applies — not a ">= 1" floor (#279 Fix 2). Only entries that were
    // actually submitted (d > DraftWindowDays) are ever eligible; both flocks
    // share the identical day-offset range so one per-flock count scales
    // directly by FlockTopologyCount.
    private static int ExpectedLockedEntryCount(DateOnly today, DateOnly farmToday, int historyDays)
    {
        var cutoff = farmToday.AddDays(-DailyEntryLockSweep.LockAfterDays);
        var lockedPerFlock = 0;
        for (var d = DraftWindowDays + 1; d <= historyDays; d++)
            if (today.AddDays(-d) < cutoff) lockedPerFlock++;
        return FlockTopologyCount * lockedPerFlock;
    }
}
