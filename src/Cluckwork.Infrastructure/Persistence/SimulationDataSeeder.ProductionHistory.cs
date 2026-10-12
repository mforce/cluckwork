using Cluckwork.Application.Modules.EggOperations.Contracts;
using Cluckwork.Domain.Modules.Farm.Contracts;

namespace Cluckwork.Infrastructure.Persistence;

public sealed partial class SimulationDataSeeder
{
    // --- Production daily-entry history (#243 later task) --------------

    // One entry per flock per day across Simulation:HistoryDays (or
    // MinSentinelAgeDays, whichever is longer — the lock-sweep proof needs
    // the sentinel regardless of how short HistoryDays is configured for a
    // test). Every date is at least 1 day older than the "today" anchor:
    // RecordDailyEntry/CreateFlock validate against the farm's OWN today
    // (IFarmClock, #35), which can already read one calendar day behind this
    // UTC anchor for a timezone west of UTC depending on time-of-day skew —
    // an entry dated exactly "today" could spuriously fail the future-date
    // rule. Starting at day 1 keeps every seeded date safely <= the farm's
    // today no matter when seeding happens to run.
    private async Task SeedProductionHistoryAsync(
        Guid accountId, DateOnly today, IReadOnlyList<Guid> flockIds, SimCast cast, SimulationOptions sim,
        CancellationToken ct)
    {
        // No ActAs here: SeedFlockHistoryAsync picks a worker per (flock, day)
        // via WorkerFor. LoadSaleableGradesAsync below is a read and audits
        // nothing.
        var grades = await LoadSaleableGradesAsync(ct);
        var historyDays = EffectiveHistoryDays(sim);

        for (var i = 0; i < flockIds.Count; i++)
            await SeedFlockHistoryAsync(
                accountId, flockIds[i], OperationalFlockInitialCounts[i], carriesExplicitAdjustments: i == 0,
                today, historyDays, grades, cast, ct);
    }

    // #943 — the birds a flock holds at the START of the day `d` days before
    // today, mirroring the two removal schedules this seeder writes: one
    // automatic mortality on every submitted day whose offset is a multiple
    // of five (`SeedFlockHistoryAsync`), and, on the first house only, the
    // 51 explicit +1 adjustments of `SeedExplicitBirdMovementsAsync` on the
    // offsets 1..63 not divisible by five. Same start-of-day convention as
    // the production report: only movements dated strictly before the day
    // count. Computed rather than read back because the history loop walks
    // newest-first, so the older days' mortality rows do not exist yet when
    // a newer day's eggs are decided.
    private static int LiveBirdsOnDay(int initialCount, int d, int historyDays, bool carriesExplicitAdjustments)
    {
        var removed = 0;
        for (var earlier = d + 1; earlier <= historyDays; earlier++)
            if (earlier > DraftWindowDays && earlier % 5 == 0) removed++;
        if (carriesExplicitAdjustments)
            for (var earlier = d + 1; earlier <= ExplicitBirdMovementLastOffset; earlier++)
                if (earlier % 5 != 0) removed++;
        return initialCount - removed;
    }

    // Live birds at an 86–94% hen-day rate that varies day to day. A hen lays
    // at most one egg a day; the former constant baseline (380 eggs from 350
    // birds) rated every day of the Dashboard and Reports over 100% (#943).
    // Deterministic variation — no Random, reproducible seeds.
    private static int EggsOnDay(int liveBirds, int d) => liveBirds * (86 + (d * 7) % 9) / 100;

    // Shared by production history (above) and the sales catalog (#243 Task
    // 3b) — both need the same saleable-grade lookup, keyed by name.
    private async Task<IReadOnlyDictionary<string, Guid>> LoadSaleableGradesAsync(CancellationToken ct)
    {
        var grades = (await eggs.ListActiveGradesAsync(SeedDefaults.FarmId, ct))
            .Where(g => g.IsSaleable)
            .ToDictionary(g => g.Name, g => g.Id);
        if (grades.Count == 0)
            throw new InvalidOperationException("Simulation seed needs the default egg grades.");
        return grades;
    }

    private async Task SeedFlockHistoryAsync(
        Guid accountId, Guid flockId, int initialCount, bool carriesExplicitAdjustments,
        DateOnly today, int historyDays,
        IReadOnlyDictionary<string, Guid> grades, SimCast cast, CancellationToken ct)
    {
        for (var d = 1; d <= historyDays; d++)
        {
            var date = today.AddDays(-d);

            // Idempotent re-run: RecordDailyEntryHandler itself only accepts
            // edits to Draft entries, so a plain re-call would throw once the
            // sentinel is Submitted/Locked — skip on existence instead
            // (mirrors EnsureUserAsync/EnsureFlockAsync in the Cast and Flocks partials).
            if (await eggsFixture.DefaultHouseEntryExistsAsync(accountId, flockId, date, ct))
                continue;

            var total = EggsOnDay(LiveBirdsOnDay(initialCount, d, historyDays, carriesExplicitAdjustments), d);
            var cracked = 4 + d % 3;
            var dirty = 2 + d % 2;
            const int discarded = 1;
            var mortality = d % 5 == 0 ? 1 : 0;
            var sellable = total - cracked - dirty - discarded;
            var large = sellable * 55 / 100;
            var medium = sellable * 30 / 100;
            var small = sellable - large - medium;

            // #500 — a worker records the day's eggs. Placed AFTER the
            // natural-key skip above so a converged re-run neither picks nor
            // resolves anyone.
            ActAs(WorkerFor(cast, flockId, d));

            var recorded = await eggs.RecordDailyEntryAsync(new RecordDailyEntryCommand(
                SeedDefaults.FarmId, SeedDefaults.HouseId, flockId, date,
                total, cracked, dirty, discarded, mortality,
                [
                    new GradeQuantityDto(grades["Large"], large),
                    new GradeQuantityDto(grades["Medium"], medium),
                    new GradeQuantityDto(grades["Small"], small),
                ]), accountId, ct);
            Require(recorded, $"record daily entry for flock {flockId} on {date:yyyy-MM-dd}");
            var entryId = recorded.Value;

            // The most recent DraftWindowDays days stay Draft; everything
            // older (including the day-9 sentinel) is submitted.
            if (d > DraftWindowDays)
            {
                // #500 — both provenance shapes. Every SubmittedByManagerEvery-th
                // day a manager signs the entry off, so its History line reads
                // "created by X, last changed by Y"; otherwise the recording
                // worker stays resolved and submits their own, which reads
                // "created and last changed by the same person".
                if (d % SubmittedByManagerEvery == 0)
                    ActAs(Pick(cast.Managers, d, cast.Owner, "Managers"));

                var submitted = await eggs.SubmitDailyEntryAsync(entryId, accountId, ct);
                Require(submitted, $"submit daily entry {entryId} for flock {flockId} on {date:yyyy-MM-dd}");
            }

            // Every handler has saved; later days reload by id, so release the
            // day's entities instead of rescanning the whole history (#1045).
            db.ChangeTracker.Clear();
        }
    }
}
