using Cluckwork.Application.Modules.FlockManagement.Contracts;
using Cluckwork.Domain.Modules.FlockManagement.Contracts;
using Cluckwork.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Cluckwork.Infrastructure.Persistence;

public sealed partial class SimulationDataSeeder
{
    // --- Minimal flock topology ----------------------------------------

    // #279 review Fix 1 (codex): both flocks must be placed strictly OLDER
    // than the deepest production-history entry SeedProductionHistoryAsync
    // writes for EVERY flock (today.AddDays(-EffectiveHistoryDays(sim))) —
    // previously House B was placed only ~70 days ago while
    // Simulation:HistoryDays defaults to 90, so House B ended up with ~20
    // days of daily entries dated BEFORE it was placed. The margin below adds
    // headroom on top of the history floor so placement is never merely
    // equal to the oldest entry date.
    private const int FlockPlacementMarginDays = 7;

    // #943 — shared with the production history, whose eggs are a lay rate
    // over these birds less the removals the seeder itself writes.
    private static readonly int[] OperationalFlockInitialCounts = [400, 350];

    private async Task<IReadOnlyList<Guid>> SeedFlockTopologyAsync(
        Guid accountId, DateOnly today, SimCast cast, SimulationOptions sim, CancellationToken ct)
    {
        // Both flocks share the identical "safely older than all history"
        // placement date — SeedFlockHistoryAsync's entry-date range
        // (today.AddDays(-d) for d = 1..EffectiveHistoryDays(sim)) is the
        // SAME for every flock, so both placements need the identical floor.
        var placementDate = today.AddDays(-FlockPlacementLookbackDays(sim));

        (string Name, string Breed, DateOnly PlacementDate, int InitialCount)[] wanted =
        [
            ("Sim House A", "ISA Brown", placementDate, OperationalFlockInitialCounts[0]),
            ("Sim House B", "Lohmann Brown", placementDate, OperationalFlockInitialCounts[1]),
        ];

        // A manager places the flocks (#500) — Flock.Create is audited, so this
        // is what the SPA's History line names. Rotates per flock so a wider
        // Managers pool spreads across the topology.
        var ids = new List<Guid>();
        for (var i = 0; i < wanted.Length; i++)
        {
            ActAs(Pick(cast.Managers, i, cast.Owner, "Managers"));
            var f = wanted[i];
            ids.Add(await EnsureFlockAsync(accountId, f.Name, f.Breed, f.PlacementDate, f.InitialCount, ct));
        }
        return ids;
    }

    private async Task<Guid> EnsureFlockAsync(
        Guid accountId, string name, string breed, DateOnly placementDate, int initialCount,
        CancellationToken ct)
    {
        // Tenant is resolved, so the query filter already scopes this to
        // accountId. Duplicate names are legal, so a second flock with this
        // name is drift the seed refuses to guess past (#852).
        switch (await flocks.ResolveByNameAsync(name, ct))
        {
            case FlockNameResolution.Found found:
                return found.Flock.Id;
            case FlockNameResolution.Ambiguous ambiguous:
                throw new InvalidOperationException(
                    $"Simulation seed: {(ambiguous.MoreExist ? "more than " : "")}{ambiguous.Candidates.Count} flocks are named {name} " +
                    $"({string.Join(", ", ambiguous.Candidates.Select(c => c.Id))}). " +
                    "Rename the duplicates or reset the fixture.");
        }

        var result = await flockModule.CreateAsync(
            new CreateFlockCommand(name, breed, placementDate, initialCount), accountId, ct);
        Require(result, $"create flock {name}");
        return result.Value;
    }

    // #627 — the explicit bird-movement offsets run through 63, so the flock
    // PLACEMENT (not the daily/feed/water/expense loop bounds — those keep
    // EffectiveHistoryDays) needs at least 64 days of lookback: every explicit
    // row must sit on/after its flock's placement date even in the shallowest
    // fixture. For the 90-day default this is 97, exactly the pre-#627 value.
    private static int FlockPlacementLookbackDays(SimulationOptions sim) =>
        Math.Max(EffectiveHistoryDays(sim) + FlockPlacementMarginDays, 64);

    // #627 — the independent picker catalog. Exactly 97 numbered Active rows
    // ("Sim Z Flock Catalog 001".."097"), the Active page-two sentinel
    // ("Sim Z Flock Page Two"), one row transitioned through
    // DepleteFlockHandler ("Sim Z Flock Depleted"), and one through
    // ArchiveFlockHandler ("Sim Z Flock Archived") — 100 Active / 1 Depleted
    // / 1 Archived on top of the two operational houses, 102 flocks total
    // (INV-5). None of these ids are ever returned to the caller, so no
    // history fanout can reach them.
    // #627 driver correction — every catalog row sorts AFTER the two
    // operational houses: "Sim Z …" > "Sim House …". That is NOT the same as
    // keeping all 100 catalog rows off page one of a 100-row default-limit
    // picker — with the 101 non-archived seed rows, page one holds both houses
    // plus the 98 earliest-sorting catalog rows, and only the lexically-last
    // sentinel ("Sim Z Flock Page Two") falls onto page two. The guarantee is
    // that the houses are INSIDE page one, not that the catalog is outside it.
    private const string FlockCatalogNamePrefix = "Sim Z Flock Catalog ";
    private const string FlockPageTwoSentinelName = "Sim Z Flock Page Two";
    private const string FlockDepletedName = "Sim Z Flock Depleted";
    private const string FlockArchivedName = "Sim Z Flock Archived";

    private async Task SeedFlockCatalogAsync(
        Guid accountId, DateOnly today, SimCast cast, SimulationOptions sim, CancellationToken ct)
    {
        var placementDate = today.AddDays(-FlockPlacementLookbackDays(sim));
        var storeKeeper = Pick(cast.Managers, 1, cast.Owner, "Managers");
        // Local actor state, not the previous phase's: Flock.Create is audited
        // (#500), so every one of the 100 catalog creations must attribute to
        // the store-keeper by THIS phase's own resolution, reorder-safe.
        ActAs(storeKeeper);

        for (var i = 1; i <= NumberedCatalogFlockCount; i++) // 97 numbered Active rows
            await EnsureFlockAsync(
                accountId,
                $"{FlockCatalogNamePrefix}{i:000}",
                "ISA Brown",
                placementDate,
                100,
                ct);

        await EnsureFlockAsync(
            accountId, FlockPageTwoSentinelName, "ISA Brown", placementDate, 100, ct);

        // Lifecycle rows: create, then transition through the real handlers —
        // the same domain rules the SPA enforces (deplete only from Active,
        // archive from Active or Depleted).
        var depletedId = await EnsureFlockAsync(
            accountId, FlockDepletedName, "Lohmann Brown", placementDate, 100, ct);
        await TransitionFlockAsync(
            depletedId, FlockDepletedName, FlockStatus.Depleted, storeKeeper,
            flockId => flockModule.DepleteAsync(flockId, ct), ct);

        var archivedId = await EnsureFlockAsync(
            accountId, FlockArchivedName, "Lohmann Brown", placementDate, 100, ct);
        await TransitionFlockAsync(
            archivedId, FlockArchivedName, FlockStatus.Archived, storeKeeper,
            flockId => flockModule.ArchiveAsync(flockId, ct), ct);
    }

    // Rerun-safe lifecycle transition: a row already in the wanted status
    // converges; a row in the WRONG status is drift the seed refuses to paper
    // over (failing closed here rather than applying a second transition or
    // silently accepting what the manifest's exact split would later reject).
    private async Task TransitionFlockAsync(
        Guid flockId, string name, FlockStatus wantedStatus, SimActor actor,
        Func<Guid, Task<Result>> transition, CancellationToken ct)
    {
        var flock = await flocks.GetAsync(flockId, ct)
            ?? throw new InvalidOperationException(
                $"Simulation seed: flock {name} was ensured but cannot be read back.");
        if (flock.Status == wantedStatus) return;

        ActAs(actor);
        var result = await transition(flockId);
        if (result.IsFailure)
            throw new InvalidOperationException(
                $"Simulation seed: flock {name} exists in status {flock.Status} but the " +
                $"transition to {wantedStatus} failed ({result.Error.Code}: {result.Error.Description}). " +
                "A lifecycle row drifted outside the seeder — fix its status or reset the fixture.");
        Require(result, $"transition flock {name} to {wantedStatus}");
    }

    // #627 — the explicit bird-adjustment band: 51 date-distinct rows on the
    // first operational flock (Sim House A), through RecordBirdMovementHandler
    // so every row carries the audit actor and the ledger's real semantics.
    //
    // The natural key is (FlockId, Date) — never the free-text note. The 51
    // offsets are the FIRST 51 positive day offsets not divisible by five, so
    // no explicit row can land on a day SeedFlockHistoryAsync generates an
    // automatic mortality row for (d % 5 == 0), and the last offset (63) is
    // inside FlockPlacementLookbackDays' 64-day floor. The final row carries a
    // stable sentinel note so the SPA ledger spec can prove the second page
    // renders — but the note is DATA, not identity.
    private const int ExplicitBirdMovementCount = 51;
    // The 51st non-multiple of five (51 rows + 12 skipped offsets); the bound
    // `LiveBirdsOnDay` walks the same band by.
    private const int ExplicitBirdMovementLastOffset = 63;
    private const string BirdMovementPageTwoSentinelNote = "Sim bird movement page two sentinel";

    private async Task SeedExplicitBirdMovementsAsync(
        Guid accountId, DateOnly today, IReadOnlyList<Guid> flockIds, SimCast cast, SimulationOptions sim,
        CancellationToken ct)
    {
        if (flockIds.Count == 0)
            throw new InvalidOperationException(
                "Simulation seed: no operational flocks exist to carry the explicit bird movements.");

        var flockId = flockIds[0];
        var storeKeeper = Pick(cast.Managers, 2, cast.Owner, "Managers");
        ActAs(storeKeeper);

        var offset = 0;
        for (var d = 1; d <= FlockPlacementLookbackDays(sim) && offset < ExplicitBirdMovementCount; d++)
        {
            if (d % 5 == 0) continue; // automatic-mortality days stay unowned by this band
            var date = today.AddDays(-d);
            var isLast = offset == ExplicitBirdMovementCount - 1;
            var note = isLast ? BirdMovementPageTwoSentinelNote : $"Simulation fixture explicit adjustment {d}";

            var exists = await flockFixture.BirdMovementExistsAsync(flockId, date, ct);
            if (!exists)
            {
                var result = await flockModule.RecordMovementAsync(new RecordBirdMovementCommand(
                    flockId, date, "Adjustment", 1, note), accountId, ct);
                Require(result, $"record explicit bird movement for flock {flockId} on {date:yyyy-MM-dd}");
            }
            offset++;
        }

        // Fail closed rather than silently publish a short band: the probe
        // above only skips rows that already exist, so a count short of 51
        // means the dates stopped being distinct.
        var count = await flockFixture.CountAdjustmentsAsync(flockId, ct);
        if (count < ExplicitBirdMovementCount)
            throw new InvalidOperationException(
                $"Simulation seed: only {count} explicit bird adjustments exist on the operational " +
                $"flock; the fixture expects exactly {ExplicitBirdMovementCount} date-distinct rows.");
    }

    // --- Restrict exactly one worker to one (not all) flocks -----------

    // Returns the pair it restricted, so the caller can rebuild the cast record.
    //
    // BOTH the early returns matter, and the idempotent one is the trap (#500):
    // it must report the EXISTING pair, never (Empty, Empty). Reporting Empty
    // leaves RestrictedFlockId empty on every run after the first, so
    // WorkerFor's `flockId == cast.RestrictedFlockId` never matches, the
    // restricted worker becomes eligible for every flock, and the seed FAILS via
    // FlockScopeGuard. A fully converged re-run hides it — the natural-key check
    // short-circuits before WorkerFor is reached — so it surfaces only on a
    // partial re-run that adds days.
    private async Task<(Guid WorkerId, Guid FlockId)> RestrictOneWorkerAsync(
        Guid accountId, SimCast cast, IReadOnlyList<Guid> flockIds, CancellationToken ct)
    {
        // Granting a flock assignment is account administration — the Owner does
        // it (#500). User.FlockAssign is audited. This phase runs directly after
        // another Owner-authored one (SeedPrimaryTimeZoneAsync), so this ActAs is
        // currently a no-op under today's ordering — kept for the same reason as
        // that phase's own comment: order-independence, not decoration.
        ActAs(cast.Owner);

        if (cast.Workers.Count == 0)
        {
            logger.LogWarning(
                "Simulation:Workers is 0 — no worker exists to flock-restrict for the load test.");
            return (Guid.Empty, Guid.Empty);
        }

        if (flockIds.Count < 2)
            throw new InvalidOperationException(
                "Simulation seed needs at least 2 flocks so the restricted worker is genuinely " +
                "narrowed (one assigned, one left out).");

        var worker = cast.Workers[0];
        var flockId = flockIds[0];

        // #606 — the interactive AssignFlockHandler now requires an
        // interactive step-up grant this trusted, non-HTTP caller cannot
        // hold, so the Access fixture writes the assignment with the SAME
        // audit shape (actor, target email, flock NAME) the handler would
        // have written — pinned by
        // SimulationSeed_RestrictedWorkerAssignment_PreservesActorAndAuditDetails.
        // An existing assignment is the idempotent re-run: the pair, not
        // Empty. See the header.
        await accessFixture.EnsureFlockAssignmentAsync(accountId, worker.UserId, worker.Email, flockId, ct);
        return (worker.UserId, flockId);
    }
}
