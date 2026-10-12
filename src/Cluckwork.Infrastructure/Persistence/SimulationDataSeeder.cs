using Cluckwork.Application.Modules.Access.Contracts;
using Cluckwork.Application.Modules.Commerce.Contracts;
using Cluckwork.Application.Modules.EggOperations.Contracts;
using Cluckwork.Application.Modules.Farm.Contracts;
using Cluckwork.Application.Modules.Finance.Contracts;
using Cluckwork.Application.Modules.FlockManagement.Contracts;
using Cluckwork.Application.Modules.GeneralInventory.Contracts;
using Cluckwork.Domain.Modules.Farm.Contracts;
using Cluckwork.Application.Common;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cluckwork.Infrastructure.Persistence;

// #243 load-test simulation seeder: the additional cast (Managers/Sales/
// Workers/ReadOnly beyond the seeded admin), a minimal flock topology, one
// flock-restricted worker, the primary account's non-UTC timezone, a second
// pristine account (a tenant-isolation fixture for the load test), and
// production daily-entry history on the Task-2 flocks with a deterministic
// proof of the automatic lock sweep. Counts/timezone/password/email-domain/
// history length live in SimulationOptions.
//
// #279: no longer self-gated on Seed:Simulation/Seed:Enabled — the ONLY
// caller is the explicit `seed --profile simulation` command (Program.cs),
// which is itself the gate (plus the Production DI-registration guard
// there), same shape as DemoDataSeeder. Unlike DemoDataSeeder, this seeder
// still does no PARTIAL-seed cleanup on failure (a load-test environment
// with a silently short cast or a missing tenant fixture is worse than one
// that refuses to report success) — but SeedAsync now catches any internal
// failure and reports it via SeedResult.Failed instead of letting it
// propagate as an unhandled exception, so the `seed` command exits non-zero
// cleanly. Idempotency comes from per-entity existence checks (mirroring
// DatabaseSeeder/DemoDataSeeder), so a clean re-run converges instead of
// erroring on "already exists".
//
// #243 Task 3d — depth/density decision (Simulation:HistoryDays defaults to
// 90): production history is already dense enough for the report/export read
// paths without any change — it's one entry per (flock, day), so the
// production report and the daily-entries/daily-entry-grades/egg-lots export
// datasets scale directly with HistoryDays (2 flocks × 90 days = 180 entries,
// ~500+ grade/lot rows once SubmitDailyEntry mints 3 graded + 2 condition
// (#396) egg lots per entry). Sales and expenses were the thin spot: Task
// 3b/3c's lifecycle
// fixtures (draft/confirmed/partially-paid orders, the 4 expenses) all sat
// inside the most recent ~week of history, so a sales/expense/profit report
// or an export request against an OLDER slice of the window saw nothing.
// SeedRecurringOrdersAsync/SeedRecurringExpensesAsync in the Sales and Inventory partials fix that with a
// second, independent, deterministic drip of confirmed orders and expenses
// spread every RecurringCadenceDays days across the WHOLE window (starting at
// MinSentinelAgeDays so at least one recurring point survives even a
// HistoryDays shorter than that floor) — ~12 extra confirmed orders and ~12
// extra expenses at the 90-day default, so a report/export over any
// representative slice of the history returns multiple rows, not a
// single-week cluster. Feed/water usage stays capped at
// FeedUsageDays/WaterUsageDays (4) — there's no report endpoint over that
// data, only a flat export dataset, and it was already non-empty; widening it
// would be bloat the task didn't ask for.
public sealed partial class SimulationDataSeeder(
    AppDbContext db,
    TenantContext tenant,
    CurrentUserContext currentUser,
    IAccessSeedLookup access,
    IAccessOperations operations,
    IAccessFixture accessFixture,
    IFlockModule flockModule,
    IFlockFixture flockFixture,
    IFlockLookup flocks,
    IFarmModule farm,
    IFarmFixture farmFixture,
    IEggOperationsModule eggs,
    IEggOperationsFixture eggsFixture,
    ICommerceModule commerce,
    ICommerceFixture commerceFixture,
    IInventoryModule inventory,
    IInventoryFixture inventoryFixture,
    IFinanceModule finance,
    IFinanceFixture financeFixture,
    DailyEntryLockSweep lockSweep,
    IClock clock,
    IServiceScopeFactory scopeFactory,
    IOptions<SimulationOptions> simulationOptions,
    ILogger<SimulationDataSeeder> logger)
{
    // The cast's worker persona. It never reaches Access: EnsureUserAsync stores it as no role.
    private const string WorkerRole = "Worker";

    // The lock sweep locks Submitted entries strictly older than
    // DailyEntryLockSweep.LockAfterDays (7) farm-local days. A day-9 entry
    // clears that boundary with a 1-day safety margin even in the worst case
    // where the farm's own "today" (IFarmClock, timezone-aware) reads one
    // calendar day behind this seeder's UTC anchor — always guaranteed
    // regardless of how short Simulation:HistoryDays is configured.
    private const int MinSentinelAgeDays = DailyEntryLockSweep.LockAfterDays + 2;

    // Shared by production history and the inventory opening purchase in the ProductionHistory and Inventory partials
    // (#243 Task 3c) — both need "how many days of history actually exist"
    // to anchor dates safely before it, regardless of how short
    // Simulation:HistoryDays is configured for a test.
    private static int EffectiveHistoryDays(SimulationOptions sim) => Math.Max(sim.HistoryDays, MinSentinelAgeDays);

    // The most recent couple of seeded days per flock stay Draft so both
    // lifecycle states exist in the seed; everything older is submitted.
    private const int DraftWindowDays = 2;

    // #500 — how often a MANAGER, rather than the worker who recorded the day,
    // submits the entry. Real farms have both shapes and #494's History line
    // renders them differently ("created and last changed by X" against
    // "created by X, last changed by Y"), so a fixture producing only one of
    // them leaves the other unexercised by every screen and every E2E spec.
    //
    // The ratio is a guess at a farm's approval rate, not a contract: no test
    // asserts 1-in-3 specifically, only that BOTH shapes occur.
    private const int SubmittedByManagerEvery = 3;

    // Deterministic id for the second, pristine tenant — mirrors
    // SeedDefaults.AccountId's fixed-GUID convention (…001) so both read as
    // related in logs/DB dumps. Never Guid.NewGuid(): a fixed id is what
    // makes "exactly two accounts after two starts" a checkable idempotency
    // guarantee instead of an ever-growing account table.
    public static readonly Guid SecondAccountId = new("0000000a-0000-0000-0000-000000000002");

    // #279: the command result (Seeded/AlreadySeeded/PrerequisitesMissing/
    // Failed) is now SEPARATE from the completion manifest artifact — the
    // manifest is still computed/validated and (when CredentialOutputPath is
    // configured) written to disk by EmitManifestAsync in the Manifest partial exactly as
    // before, but SeedAsync's return value no longer carries it; a caller
    // that needs the manifest's contents reads the file (or, in a test with
    // no CredentialOutputPath configured, there is none to read — point it at
    // a real path instead).
    public async Task<SeedResult> SeedAsync(CancellationToken ct = default)
    {
        // #279 review Fix 4 (codex): the WHOLE operational body — options
        // access, the prerequisite queries, tenant.Resolve, and all seeding —
        // runs inside this catch boundary, so an exception from ANY of them
        // (e.g. the DB is unreachable during preflight) becomes a clean
        // SeedResult.Failed and a non-zero command exit, never an unhandled
        // throw escaping the `seed` command. The explicit PrerequisitesMissing
        // returns below still short-circuit from inside the try.
        try
        {
            var sim = simulationOptions.Value;
            if (string.IsNullOrWhiteSpace(sim.CastPassword))
            {
                const string prereqMessage =
                    "Simulation seed prerequisites missing: Simulation:CastPassword is not set. " +
                    "The sim cast has no fallback credential.";
                logger.LogError(prereqMessage);
                return SeedResult.PrerequisitesMissing(prereqMessage);
            }

            var accountId = SeedDefaults.AccountId;

            // Preflight the base prerequisites (mirrors DemoDataSeeder's own
            // MissingBaseDataAsync, #284 review): the simulation needs the
            // default account, the Admin role, the three saleable egg grades it
            // consumes by name (all #283 migration-baked static reference data,
            // so these three should never actually be missing against a
            // current schema — kept as defense-in-depth), AND an existing
            // enabled Owner-role user in the default account (used below as the
            // fixture's author — this seeder never creates a second Owner).
            // Unlike the first three, the Owner user is a REAL prerequisite: it
            // comes only from the `bootstrap-admin` first-run command (#283),
            // which the `seed` command never runs. Checked BEFORE
            // tenant.Resolve, so every tenant-scoped query needs
            // IgnoreQueryFilters (same reasoning as DemoDataSeeder's preflight).
            //
            // #500 — the Owner is looked up ONCE, here, and that single result
            // both gates the preflight and signs the fixture. Querying again
            // after the preflight could disagree with it (an Owner disabled or
            // reassigned between the two) and the seeder would then act as a
            // user its own preflight never approved.
            var (ownerRoleExists, owner, disabledOwners) = await FindOwnerAsync(accountId, ct);

            // #500 (codex round 4) — the base-data check and the Owner check are
            // now SEPARATE blocks, exactly as DemoDataSeeder has always had them.
            // Collapsing them into one `if` was the defect: a single message had
            // to serve two unrelated causes, so it named ONE remedy and got the
            // other case wrong. Concretely — grades are user-renamable (#283), so
            // a renamed `Large` with a perfectly good enabled Owner landed in the
            // combined block and was told to run `bootstrap-admin`, which returns
            // AlreadyProvisioned whenever any Owner exists and therefore loops
            // forever. Two causes, two messages; the split is what keeps each
            // remedy performable from the state that printed it.
            if (await MissingBaseDataAsync(accountId, ct))
            {
                // Splitting the block bought accurate advice per cause, but it
                // also short-circuits — so when BOTH the base data and the Owner
                // are missing, this returns before the Owner check ever runs and
                // the operator would repair the grades, re-run, and only THEN
                // discover the second problem. Two trips for one broken
                // database. The Owner is already in hand from the single
                // FindOwnerAsync lookup, so say both at once. This appendix is
                // what keeps "each message names a remedy that can be followed"
                // true for the dual-failure case rather than only the common one.
                //
                // Gated on the ROLE existing (#500, codex round 5). Without that
                // it fires for a schema whose migration-baked roles were dropped
                // — `GetUsersInRoleAsync` returns empty rather than throwing, so
                // that state is indistinguishable here from "nobody is an Owner
                // yet" — and appends `bootstrap-admin`, which cannot create a
                // role. Restoring the base data, which this message already
                // says, IS the whole remedy in that case.
                var alsoMissingOwner = owner is null && ownerRoleExists
                    ? disabledOwners > 0
                        ? " AND, separately: the Owner role here is held only by DISABLED user(s), so once the " +
                          "base data is repaired this will still refuse. Clear BOTH DisabledAt and DisabledBy " +
                          "for that user directly in the database — there is no in-product repair for that state."
                        : " AND, separately: this account has no user in the Owner role, so once the base data " +
                          "is repaired this will still refuse. Run `dotnet Cluckwork.Api.dll bootstrap-admin " +
                          "--email <e>` as well."
                    : string.Empty;

                var baseDataMessage =
                    "Simulation seed prerequisites missing: the base data (default account, Admin role, and " +
                    "the saleable Large/Medium/Small egg grades this fixture consumes BY NAME) is not fully " +
                    "present. It ships as part of the EF migrations (#283) — but the grades are user-renamable, " +
                    "so a renamed or deleted saleable grade lands here too, and that is the likelier cause on a " +
                    "database that has been used. Restore the Large/Medium/Small saleable grade names (or run " +
                    "`migrate` against a current schema), then re-run `seed --profile simulation`. " +
                    "`bootstrap-admin` does NOT repair this: it only provisions an Owner." + alsoMissingOwner;
                logger.LogError(baseDataMessage);
                return SeedResult.PrerequisitesMissing(baseDataMessage);
            }

            if (owner is null)
            {
                // Same split as DemoDataSeeder, for the same reason: pointing an
                // operator whose only Owner is DISABLED at `bootstrap-admin`
                // sends them in a circle, because it counts Owner role rows
                // without checking DisabledAt and exits 0 having done nothing.
                var prereqMessage = disabledOwners > 0
                    ? "Simulation seed prerequisites missing: the default account's Owner role is held only by " +
                      "DISABLED user(s), so the fixture would be signed by an account that cannot log in. There " +
                      "is no in-product repair for this state today: `bootstrap-admin` reports 'already " +
                      "provisioned', `recover-admin` refuses a disabled target, and the Users screen that could " +
                      "re-enable them is Owner-only, which nobody can now reach. Clear BOTH DisabledAt and " +
                      "DisabledBy for that user directly in the database — they describe one fact and the app " +
                      "always clears them together — then re-run `seed --profile simulation`."
                    : "Simulation seed prerequisites missing: the default account has no user in the Owner " +
                      "role, so the fixture would have no author. The account/role/grades ship with the EF " +
                      "migrations (#283); the Owner admin does not — run `dotnet Cluckwork.Api.dll " +
                      "bootstrap-admin --email <e>` against this database, then re-run " +
                      "`seed --profile simulation`.";
                logger.LogError(prereqMessage);
                return SeedResult.PrerequisitesMissing(prereqMessage);
            }

            // Handlers and query filters need the tenant, which is unresolved at
            // startup — resolve it to the seeded account for this scope (matches
            // DemoDataSeeder).
            tenant.Resolve(accountId);

            // #500 — the actor, which IAuditWriter now requires. The Owner opens
            // the fixture and authors the cast itself; every later phase declares
            // its own persona via ActAs, and the daily-entry workers arrive
            // through WorkerFor, which is subject to FlockScopeGuard — the same
            // context read as an authorization input, not merely an audit label.
            //
            // No re-check of `owner is null` here: the preflight above returns
            // on that case, so the compiler's own flow analysis proves it
            // non-null from this point. The former guard was written when one
            // combined `if` made that non-obvious; a restated invariant the
            // language already enforces is worse than none, because it implies
            // the branch is reachable.
            //
            // Roles come from Access, never from a literal: SimActor.Roles is
            // an authorization input (FlockScopeGuard), so the fixture must act
            // with the roles this user ACTUALLY holds, not the ones the preflight
            // searched by.
            var selectedOwner = await access.GetActorAsync(accountId, owner.Id, ct)
                ?? throw new InvalidOperationException("Simulation seed: the selected Owner cannot be read back.");
            var ownerActor = new SimActor(selectedOwner.Id, selectedOwner.Email!, selectedOwner.Roles);
            ActAs(ownerActor);

            // #279 review (codex re-check): the anchor AND the completion signal
            // are a DURABLE row (SimulationSeedState), not inferred from fixture
            // data or from SecondAccountId. Written BEFORE any dated fixture row,
            // so the anchor survives (a) a UTC-midnight rollover, (b) foreign
            // daily entries a load test writes into this account (which would
            // poison a max(entry-date) recovery), and (c) a crash before the
            // first entry exists. `today` is re-read from the row on every
            // re-run, so every date-relative natural key re-derives identically
            // and the fixture converges instead of growing a shifted copy. Only
            // a genuine first run (no row yet) takes clock.TodayUtc.
            // Tenant is resolved, so the query filter already scopes this to
            // accountId (SimulationSeedState carries the same filter as every
            // other AccountId-bearing entity — #279 review).
            var state = await db.SimulationSeedStates
                .FirstOrDefaultAsync(s => s.AccountId == accountId, ct);
            DateOnly today;
            // Whether a PRIOR run reached completion (its manifest succeeded). A
            // run interrupted after writing fixtures but before the manifest left
            // the marker null, so its retry is correctly NOT treated as a no-op.
            bool priorRunCompleted;
            if (state is null)
            {
                today = clock.TodayUtc;
                state = new SimulationSeedState { AccountId = accountId, Anchor = today };
                db.SimulationSeedStates.Add(state);
                await db.SaveChangesAsync(ct); // persist the anchor BEFORE seeding
                priorRunCompleted = false;
            }
            else
            {
                today = state.Anchor;
                priorRunCompleted = state.CompletedAtUtc is not null;
            }

            // #279 review (codex re-check): "AlreadySeeded" must mean a genuine
            // idempotent no-op. The completion marker alone can't tell a plain
            // re-run from one where SimulationOptions changed (e.g. Managers
            // 1→2) so that THIS run created new fixtures — both leave the marker
            // set. So snapshot the counts BEFORE seeding; the run is AlreadySeeded
            // only if a prior run completed AND this run changed nothing (the
            // final manifest counts equal the pre-seed counts). A first run, an
            // interrupted prior run, or a definition change all report Seeded.
            var (countsBeforeSeed, _) = await ComputeCountsAsync(accountId, ct);

            var cast = await SeedCastAsync(accountId, ownerActor, sim, ct);
            // #627 — the topology stays EXACTLY the two operational houses.
            // The picker catalog is a separate phase below: its rows must never
            // reach the daily/feed/water/expense fanout that runs on flockIds.
            var flockIds = await SeedFlockTopologyAsync(accountId, today, cast, sim, ct);
            await SeedFlockCatalogAsync(accountId, today, cast, sim, ct);
            await SeedExplicitBirdMovementsAsync(accountId, today, flockIds, cast, sim, ct);
            // Timezone BEFORE any dated data exists (see SeedPrimaryTimeZoneAsync)
            // — production history below must see the account's real timezone.
            //
            // #500 — this phase moved AHEAD of RestrictOneWorkerAsync. The two are
            // order-independent of each other and the timezone still precedes
            // every dated fixture row; what changes is that it now follows a
            // MANAGER-authored phase. While it followed RestrictOneWorkerAsync
            // (Owner-authored) its own ActAs(Owner) was a no-op — deleting it
            // changed nothing observable, so no test could pin it.
            await SeedPrimaryTimeZoneAsync(cast, sim, ct);
            var (restrictedWorkerId, restrictedFlockId) =
                await RestrictOneWorkerAsync(accountId, cast, flockIds, ct);
            cast = cast with
            {
                RestrictedWorkerId = restrictedWorkerId,
                RestrictedFlockId = restrictedFlockId,
            };
            await SeedProductionHistoryAsync(accountId, today, flockIds, cast, sim, ct);
            // Inventory before feed usage: feed usage draws down a feed lot, so
            // the item + opening purchase must exist first (#243 Task 3c).
            await SeedInventoryOperationsAsync(accountId, today, flockIds, cast, sim, ct);
            await SeedSalesAsync(accountId, today, cast, sim, ct);
            await SeedSecondAccountAsync(ct);

            // Deterministic lock-sweep proof: run the sweep synchronously as part
            // of seeding rather than waiting on the DurableJobWorker's 30s poll,
            // so the day-9 sentinel entry seeded above is already Locked by the
            // time SeedAsync returns.
            await DrainLockSweepAsync(sim, ct);

            // Completion manifest (#243 Task 3e) — always last: counts +
            // validates the whole fixture above, then (only when a manifest path
            // is configured) writes the artifact the #243 findings header and
            // #277's Playwright suite read. Fail-closed: ValidateCounts throws on
            // ANY shortfall, so a partial seed never gets a "complete" manifest —
            // caught below, same as any other internal failure.
            var manifest = await EmitManifestAsync(accountId, today, sim, ct);

            // Durable completion marker — set ONLY now that exact validation +
            // manifest emission have succeeded, and only once (a first completion
            // stamps it; later re-runs leave it). The timestamp itself isn't
            // validated/fingerprinted, so its value is free to differ per run.
            // The per-day clear detached the durable completion row.
            state = await db.SimulationSeedStates.SingleAsync(s => s.AccountId == accountId, ct);
            if (state.CompletedAtUtc is null)
            {
                state.CompletedAtUtc = new DateTimeOffset(clock.UtcNow, TimeSpan.Zero);
                await db.SaveChangesAsync(ct);
            }

            // AlreadySeeded == a prior run completed AND this run was a no-op. The
            // manifest counts are wall-clock-stable (the Draft/Submitted/Locked
            // split lives in LifecycleStates, not Counts), so a plain re-run
            // matches while a definition change does not.
            var wasComplete = priorRunCompleted && countsBeforeSeed.Equals(manifest.Counts);

            var message = wasComplete
                ? $"Simulation seed already present; converged (fingerprint {manifest.Fingerprint})."
                : $"Simulation data seeded (fingerprint {manifest.Fingerprint}).";
            logger.LogInformation(message);
            return wasComplete ? SeedResult.AlreadySeeded(message) : SeedResult.Seeded(message);
        }
        catch (Exception ex)
        {
            // Fail-loud but caught (#279): a load-test environment with a
            // silently short cast or a missing tenant fixture is worse than
            // one that refuses to report success, but the `seed` command
            // still needs a clean non-zero exit instead of an unhandled
            // exception. No partial-seed cleanup (unlike DemoDataSeeder) —
            // every step's own existence check makes a subsequent re-run
            // converge instead of erroring on "already exists".
            logger.LogError(ex, "Simulation seed failed.");
            return SeedResult.Failed($"Simulation seed failed: {ex.Message}");
        }
    }

    private static void Require(Result result, string what)
    {
        if (result.IsFailure)
            throw new InvalidOperationException(
                $"Simulation seed step failed ({what}): {result.Error.Code} — {result.Error.Description}");
    }

    private static void Require(Result<Guid> result, string what) =>
        Require(result.IsSuccess ? Result.Success() : Result.Failure(result.Error), what);

    private static void Require(Result<SubmitDailyEntryResponse> result, string what) =>
        Require(result.IsSuccess ? Result.Success() : Result.Failure(result.Error), what);

    private static void Require(Result<ConfirmSaleResponse> result, string what) =>
        Require(result.IsSuccess ? Result.Success() : Result.Failure(result.Error), what);

    private static void Require(Result<RecordFeedUsageResponse> result, string what) =>
        Require(result.IsSuccess ? Result.Success() : Result.Failure(result.Error), what);
}
