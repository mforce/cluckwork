namespace Cluckwork.Infrastructure.Persistence;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Accounts;
using Cluckwork.Application.Features.Accounts.UpdateFarmSettings;
using Cluckwork.Application.Features.Catalog.CreateProduct;
using Cluckwork.Application.Features.Customers.CreateCustomer;
using Cluckwork.Application.Features.DailyEntries;
using Cluckwork.Application.Features.DailyEntries.RecordDailyEntry;
using Cluckwork.Application.Features.DailyEntries.SubmitDailyEntry;
using Cluckwork.Application.Features.EggGrades;
using Cluckwork.Application.Features.Expenses.CreateExpense;
using Cluckwork.Application.Features.Expenses.CreateExpenseCategory;
using Cluckwork.Application.Features.Flocks.CreateFlock;
using Cluckwork.Application.Features.Inventory;
using Cluckwork.Application.Features.Inventory.CreateInventoryItem;
using Cluckwork.Application.Features.Inventory.RecordAdjustment;
using Cluckwork.Application.Features.Inventory.RecordFeedUsage;
using Cluckwork.Application.Features.Inventory.RecordPurchase;
using Cluckwork.Application.Features.Inventory.RecordWaterUsage;
using Cluckwork.Application.Features.Sales.AddOrderItem;
using Cluckwork.Application.Features.Sales.ConfirmSale;
using Cluckwork.Application.Features.Sales.CreateSalesOrder;
using Cluckwork.Application.Features.Sales.RecordPayment;
using Cluckwork.Application.Features.Users;
using Cluckwork.Application.Features.Users.AssignFlock;
using Cluckwork.Application.Features.Users.CreateUser;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Eggs;
using Cluckwork.Domain.Inventory;
using Cluckwork.Domain.Sales;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Jobs;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
// ~500+ grade/lot rows once SubmitDailyEntry mints up to 3 egg lots per
// entry). Sales and expenses were the thin spot: Task 3b/3c's lifecycle
// fixtures (draft/confirmed/partially-paid orders, the 4 expenses) all sat
// inside the most recent ~week of history, so a sales/expense/profit report
// or an export request against an OLDER slice of the window saw nothing.
// SeedRecurringOrdersAsync/SeedRecurringExpensesAsync below fix that with a
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
public sealed class SimulationDataSeeder(
    AppDbContext db,
    TenantContext tenant,
    UserManager<ApplicationUser> users,
    CreateUserHandler createUser,
    CreateFlockHandler createFlock,
    AssignFlockHandler assignFlock,
    IUserRoleAssignmentRepository assignments,
    IAccountRepository accounts,
    UpdateFarmSettingsHandler updateFarmSettings,
    IEggGradeRepository eggGrades,
    IDailyEntryRepository dailyEntries,
    RecordDailyEntryHandler recordEntry,
    SubmitDailyEntryHandler submitEntry,
    CreateProductHandler createProduct,
    CreateCustomerHandler createCustomer,
    CreateSalesOrderHandler createSalesOrder,
    AddOrderItemHandler addOrderItem,
    ConfirmSaleHandler confirmSale,
    RecordPaymentHandler recordPayment,
    IInventoryItemRepository inventoryItems,
    CreateInventoryItemHandler createInventoryItem,
    RecordPurchaseHandler recordPurchase,
    RecordAdjustmentHandler recordAdjustment,
    RecordFeedUsageHandler recordFeedUsage,
    RecordWaterUsageHandler recordWaterUsage,
    CreateExpenseCategoryHandler createExpenseCategory,
    CreateExpenseHandler createExpense,
    DailyEntryLockSweep lockSweep,
    IClock clock,
    IOptions<SimulationOptions> simulationOptions,
    ILogger<SimulationDataSeeder> logger)
{
    // The lock sweep locks Submitted entries strictly older than
    // DailyEntryLockSweep.LockAfterDays (7) farm-local days. A day-9 entry
    // clears that boundary with a 1-day safety margin even in the worst case
    // where the farm's own "today" (IFarmClock, timezone-aware) reads one
    // calendar day behind this seeder's UTC anchor — always guaranteed
    // regardless of how short Simulation:HistoryDays is configured.
    private const int MinSentinelAgeDays = DailyEntryLockSweep.LockAfterDays + 2;

    // Shared by production history and the inventory opening purchase below
    // (#243 Task 3c) — both need "how many days of history actually exist"
    // to anchor dates safely before it, regardless of how short
    // Simulation:HistoryDays is configured for a test.
    private static int EffectiveHistoryDays(SimulationOptions sim) => Math.Max(sim.HistoryDays, MinSentinelAgeDays);

    // The most recent couple of seeded days per flock stay Draft so both
    // lifecycle states exist in the seed; everything older is submitted.
    private const int DraftWindowDays = 2;

    // Deterministic id for the second, pristine tenant — mirrors
    // SeedDefaults.AccountId's fixed-GUID convention (…001) so both read as
    // related in logs/DB dumps. Never Guid.NewGuid(): a fixed id is what
    // makes "exactly two accounts after two starts" a checkable idempotency
    // guarantee instead of an ever-growing account table.
    public static readonly Guid SecondAccountId = new("0000000a-0000-0000-0000-000000000002");

    // #279: the command result (Seeded/AlreadySeeded/PrerequisitesMissing/
    // Failed) is now SEPARATE from the completion manifest artifact — the
    // manifest is still computed/validated and (when CredentialOutputPath is
    // configured) written to disk by EmitManifestAsync below exactly as
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
            // Owner-role user in the default account (reused below as Owner —
            // this seeder never creates a second Owner). Unlike the first
            // three, the Owner user is a REAL prerequisite: it comes only from
            // the `bootstrap-admin` first-run command (#283), which the `seed`
            // command never runs. Checked BEFORE tenant.Resolve, so every
            // tenant-scoped query needs IgnoreQueryFilters (same reasoning as
            // DemoDataSeeder's preflight).
            var missingBaseData = await MissingBaseDataAsync(accountId, ct);
            if (missingBaseData)
            {
                var prereqMessage =
                    "Simulation seed prerequisites missing: the base data (default account, Admin role, " +
                    "the saleable Large/Medium/Small egg grades, and an admin in the Owner role) is not " +
                    "fully present. The account/role/grades ship with the EF migrations (#283); the Owner " +
                    "admin does not — run `dotnet Cluckwork.Api.dll bootstrap-admin --email <e>` against " +
                    "this database, then re-run `seed --profile simulation`.";
                logger.LogError(prereqMessage);
                return SeedResult.PrerequisitesMissing(prereqMessage);
            }

            // Handlers and query filters need the tenant, which is unresolved at
            // startup — resolve it to the seeded account for this scope (matches
            // DemoDataSeeder).
            tenant.Resolve(accountId);

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

            var workerIds = await SeedCastAsync(accountId, sim, ct);
            var flockIds = await SeedFlockTopologyAsync(accountId, today, sim, ct);
            await RestrictOneWorkerAsync(accountId, workerIds, flockIds, ct);
            // Timezone BEFORE any dated data exists (see SeedPrimaryTimeZoneAsync)
            // — production history below must see the account's real timezone.
            await SeedPrimaryTimeZoneAsync(sim, ct);
            await SeedProductionHistoryAsync(accountId, today, flockIds, sim, ct);
            // Inventory before feed usage: feed usage draws down a feed lot, so
            // the item + opening purchase must exist first (#243 Task 3c).
            await SeedInventoryOperationsAsync(accountId, today, flockIds, sim, ct);
            await SeedSalesAsync(accountId, today, sim, ct);
            await SeedSecondAccountAsync(ct);

            // Deterministic lock-sweep proof: run the sweep synchronously as part
            // of seeding rather than waiting on the DurableJobWorker's 30s poll,
            // so the day-9 sentinel entry seeded above is already Locked by the
            // time SeedAsync returns.
            await lockSweep.RunAsync(ct);

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


    // The three saleable grades SeedFlockHistoryAsync/SeedSalesAsync consume by
    // name (grades["Large"/"Medium"/"Small"]). The #283 migration bakes them
    // in; preflight requires all three present AND saleable so a schema
    // missing one fails loud here instead of throwing KeyNotFoundException
    // mid-seed (#279 review Fix 3).
    private static readonly string[] RequiredSaleableGrades = ["Large", "Medium", "Small"];

    // Existence + shape checks, run BEFORE tenant.Resolve — every tenant-scoped
    // query needs IgnoreQueryFilters (mirrors DemoDataSeeder's own
    // MissingBaseDataAsync). #279 review Fix 3 (codex): each check now PROVES the
    // specific base datum this seeder depends on rather than a weaker proxy —
    // the exact saleable Large/Medium/Small grades (not merely "any grade").
    // #283 — the Owner check no longer takes an email: Seed:AdminEmail is
    // retired along with the runtime seeder that read it, so this asks the
    // question the seeder actually cares about — does ANY Owner-role user
    // exist in the default account — via GetUsersInRoleAsync rather than a
    // configured address. Reused below as the single Owner; this seeder never
    // creates a second one.
    private async Task<bool> MissingBaseDataAsync(Guid accountId, CancellationToken ct)
    {
        var accountExists = await db.Accounts
            .IgnoreQueryFilters()
            .AnyAsync(a => a.Id == accountId, ct);
        if (!accountExists) return true;

        var adminRoleExists = await db.Roles.AnyAsync(r => r.Name == Roles.Owner, ct);
        if (!adminRoleExists) return true;

        // Tenant is unresolved here, so IgnoreQueryFilters + explicit AccountId
        // (the repository's ListActiveAsync would apply the tenant filter and
        // see nothing).
        var saleableGradeNames = await db.EggGrades
            .IgnoreQueryFilters()
            .Where(g => g.AccountId == accountId && g.IsSaleable)
            .Select(g => g.Name)
            .ToListAsync(ct);
        if (!RequiredSaleableGrades.All(saleableGradeNames.Contains)) return true;

        var owners = await users.GetUsersInRoleAsync(Roles.Owner);
        return !owners.Any(u => u.AccountId == accountId);
    }

    // --- Cast: Managers, Sales, ReadOnly, Workers (role-less) ---------

    private async Task<IReadOnlyList<Guid>> SeedCastAsync(
        Guid accountId, SimulationOptions sim, CancellationToken ct)
    {
        for (var i = 1; i <= sim.Managers; i++)
            await EnsureUserAsync(
                accountId, $"sim-manager-{i}@{sim.EmailDomain}", Roles.Manager,
                $"Sim Manager {i}", sim.CastPassword, ct);

        for (var i = 1; i <= sim.Sales; i++)
            await EnsureUserAsync(
                accountId, $"sim-sales-{i}@{sim.EmailDomain}", Roles.Sales,
                $"Sim Sales {i}", sim.CastPassword, ct);

        for (var i = 1; i <= sim.ReadOnly; i++)
            await EnsureUserAsync(
                accountId, $"sim-readonly-{i}@{sim.EmailDomain}", Roles.ReadOnly,
                $"Sim ReadOnly {i}", sim.CastPassword, ct);

        // Workers deliberately carry no role row (Roles.cs) — CreateUserHandler
        // maps CreateUserValidator.WorkerRole to a null role.
        var workerIds = new List<Guid>();
        for (var i = 1; i <= sim.Workers; i++)
            workerIds.Add(await EnsureUserAsync(
                accountId, $"sim-worker-{i}@{sim.EmailDomain}", CreateUserValidator.WorkerRole,
                $"Sim Worker {i}", sim.CastPassword, ct));

        return workerIds;
    }

    private async Task<Guid> EnsureUserAsync(
        Guid accountId, string email, string role, string name, string password, CancellationToken ct)
    {
        var existing = await users.FindByEmailAsync(email);
        if (existing is not null) return existing.Id;

        // #308 — actingUserId only matters for the Role==Owner step-up gate,
        // and this seeder never creates one (see the "Cast" comment above:
        // Managers, Sales, ReadOnly, Workers only) — Guid.Empty is inert here.
        var result = await createUser.HandleAsync(
            new CreateUserCommand(email, password, role, name), accountId, Guid.Empty, ct);
        Require(result, $"create cast user {email}");
        return result.Value;
    }

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

    private async Task<IReadOnlyList<Guid>> SeedFlockTopologyAsync(
        Guid accountId, DateOnly today, SimulationOptions sim, CancellationToken ct)
    {
        // Both flocks share the identical "safely older than all history"
        // placement date — SeedFlockHistoryAsync's entry-date range
        // (today.AddDays(-d) for d = 1..EffectiveHistoryDays(sim)) is the
        // SAME for every flock, so both placements need the identical floor.
        var placementDate = today.AddDays(-(EffectiveHistoryDays(sim) + FlockPlacementMarginDays));

        (string Name, string Breed, DateOnly PlacementDate, int InitialCount)[] wanted =
        [
            ("Sim House A", "ISA Brown", placementDate, 400),
            ("Sim House B", "Lohmann Brown", placementDate, 350),
        ];

        var ids = new List<Guid>();
        foreach (var f in wanted)
            ids.Add(await EnsureFlockAsync(accountId, f.Name, f.Breed, f.PlacementDate, f.InitialCount, ct));
        return ids;
    }

    private async Task<Guid> EnsureFlockAsync(
        Guid accountId, string name, string breed, DateOnly placementDate, int initialCount,
        CancellationToken ct)
    {
        // Tenant is resolved, so the query filter already scopes this to
        // accountId — matches how DemoDataSeeder probes for existing rows.
        var existing = await db.Flocks.FirstOrDefaultAsync(f => f.Name == name, ct);
        if (existing is not null) return existing.Id;

        var result = await createFlock.HandleAsync(
            new CreateFlockCommand(name, breed, placementDate, initialCount), accountId, ct);
        Require(result, $"create flock {name}");
        return result.Value;
    }

    // --- Restrict exactly one worker to one (not all) flocks -----------

    private async Task RestrictOneWorkerAsync(
        Guid accountId, IReadOnlyList<Guid> workerIds, IReadOnlyList<Guid> flockIds, CancellationToken ct)
    {
        if (workerIds.Count == 0)
        {
            logger.LogWarning(
                "Simulation:Workers is 0 — no worker exists to flock-restrict for the load test.");
            return;
        }

        if (flockIds.Count < 2)
            throw new InvalidOperationException(
                "Simulation seed needs at least 2 flocks so the restricted worker is genuinely " +
                "narrowed (one assigned, one left out).");

        var workerId = workerIds[0];
        var flockId = flockIds[0];

        var existingAssignments = await assignments.ListByUserAsync(workerId, ct);
        if (existingAssignments.Any(a => a.FlockId == flockId)) return; // idempotent re-run

        var result = await assignFlock.HandleAsync(workerId, flockId, accountId, ct);
        Require(result, $"restrict worker {workerId} to flock {flockId}");
    }

    // --- Primary account timezone (BEFORE any dated data exists) -------

    private async Task SeedPrimaryTimeZoneAsync(SimulationOptions sim, CancellationToken ct)
    {
        var account = await accounts.GetCurrentTrackedAsync(ct)
            ?? throw new InvalidOperationException("Simulation seed: the primary account was not found.");

        if (string.Equals(account.TimeZoneId, sim.TimeZoneId, StringComparison.Ordinal))
            return; // already set — idempotent re-run.

        var command = new UpdateFarmSettingsCommand(
            account.Name,
            sim.TimeZoneId,
            account.Locale,
            account.DefaultCurrencyCode,
            account.UnitSystem.ToString(),
            account.FirstDayOfWeek?.ToString(),
            account.DateFormatOverride,
            account.TimeFormatOverride,
            account.Brand,
            account.Version);

        var result = await updateFarmSettings.HandleAsync(command, ct);
        Require(result, $"set primary account timezone to {sim.TimeZoneId}");
    }

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
        Guid accountId, DateOnly today, IReadOnlyList<Guid> flockIds, SimulationOptions sim,
        CancellationToken ct)
    {
        var grades = await LoadSaleableGradesAsync(ct);
        var historyDays = EffectiveHistoryDays(sim);

        for (var i = 0; i < flockIds.Count; i++)
            await SeedFlockHistoryAsync(accountId, flockIds[i], baseline: 320 + i * 60, today, historyDays, grades, ct);
    }

    // Shared by production history (above) and the sales catalog (#243 Task
    // 3b) — both need the same saleable-grade lookup, keyed by name.
    private async Task<IReadOnlyDictionary<string, Guid>> LoadSaleableGradesAsync(CancellationToken ct)
    {
        var grades = (await eggGrades.ListActiveAsync(SeedDefaults.FarmId, ct))
            .Where(g => g.IsSaleable)
            .ToDictionary(g => g.Name, g => g.Id);
        if (grades.Count == 0)
            throw new InvalidOperationException("Simulation seed needs the default egg grades.");
        return grades;
    }

    private async Task SeedFlockHistoryAsync(
        Guid accountId, Guid flockId, int baseline, DateOnly today, int historyDays,
        IReadOnlyDictionary<string, Guid> grades, CancellationToken ct)
    {
        for (var d = 1; d <= historyDays; d++)
        {
            var date = today.AddDays(-d);

            // Idempotent re-run: RecordDailyEntryHandler itself only accepts
            // edits to Draft entries, so a plain re-call would throw once the
            // sentinel is Submitted/Locked — skip on existence instead
            // (mirrors EnsureUserAsync/EnsureFlockAsync above).
            var existing = await dailyEntries.FindByNaturalKeyAsync(
                accountId, SeedDefaults.FarmId, SeedDefaults.HouseId, flockId, date, ct);
            if (existing is not null) continue;

            // Deterministic variation — no Random, reproducible seeds.
            var total = baseline + (d * 7) % 23;
            var cracked = 4 + d % 3;
            var dirty = 2 + d % 2;
            const int discarded = 1;
            var mortality = d % 5 == 0 ? 1 : 0;
            var sellable = total - cracked - dirty - discarded;
            var large = sellable * 55 / 100;
            var medium = sellable * 30 / 100;
            var small = sellable - large - medium;

            var recorded = await recordEntry.HandleAsync(new RecordDailyEntryCommand(
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
                var submitted = await submitEntry.HandleAsync(entryId, accountId, ct);
                Require(submitted, $"submit daily entry {entryId} for flock {flockId} on {date:yyyy-MM-dd}");
            }
        }
    }

    // --- Sales: catalog, customers, orders across the lifecycle, FIFO
    // depletion, payments (#243 Task 3b) ------------------------------------
    //
    // Everything below runs through the SAME real handlers DemoDataSeeder
    // uses (CreateProduct/CreateCustomer/CreateSalesOrder/AddOrderItem/
    // ConfirmSale/RecordPayment) — orders confirmed here allocate FIFO
    // against the graded lots the production history above just generated,
    // exactly as a user clicking through the Sales screen would. Idempotency
    // is per-entity existence checks (product/customer by Name, order by the
    // (CustomerId, OrderDate) natural key this seeder controls), same style
    // as EnsureUserAsync/EnsureFlockAsync above.

    // One product per saleable grade, sold in individual eggs (factor 1) —
    // keeps order-item quantities directly comparable to lot sizes, which is
    // what makes the FIFO-contention shape below easy to reason about.
    private static readonly (string GradeName, string ProductName, long PriceMinorUnits)[] CatalogWanted =
    [
        ("Large", "Sim Large Eggs", 45),
        ("Medium", "Sim Medium Eggs", 38),
        ("Small", "Sim Small Eggs", 30),
    ];

    private static readonly (string Name, string Phone, string Note)[] CustomersWanted =
    [
        ("Sim Customer 1", "555-0201", "Simulation fixture customer"),
        ("Sim Customer 2", "555-0202", "Simulation fixture customer"),
        ("Sim Customer 3", "555-0203", "Simulation fixture customer"),
    ];

    // Modest relative to a single day's Large lot (baseline 320+ eggs/day,
    // ~55% Large) — small enough that every confirmed order below draws from
    // the SAME shallow pool of the oldest available Large lots (FIFO always
    // starts at the oldest ProductionDate) instead of each landing on
    // disjoint fresh stock. That shared-pool shape is what a later hazard
    // task needs to force FOR UPDATE lock contention; this task only sets
    // the baseline shape up and never forces the contention itself.
    private const int ConfirmedOrderQuantityEggs = 36;
    private const int DraftOrderQuantityEggs = 24;

    // #243 Task 3d — see the class header's depth/density decision. This
    // second series never touches days 1-7 (every hardcoded date above),
    // starts at MinSentinelAgeDays (9, so it survives even a HistoryDays
    // shorter than that floor), and repeats every RecurringCadenceDays out to
    // the effective history length — spreading confirmed orders across the
    // WHOLE window instead of leaving them clustered in the most recent week.
    private const int RecurringStartDay = MinSentinelAgeDays;
    private const int RecurringCadenceDays = 7;
    private const int RecurringOrderQuantityEggs = 12;

    private async Task SeedSalesAsync(Guid accountId, DateOnly today, SimulationOptions sim, CancellationToken ct)
    {
        var grades = await LoadSaleableGradesAsync(ct);
        var products = await SeedProductCatalogAsync(accountId, grades, ct);
        var customerIds = await SeedCustomersAsync(accountId, ct);

        var largeProductId = products["Large"];
        var mediumProductId = products["Medium"];
        var customer1 = customerIds[0];
        var customer2 = customerIds[1];
        var customer3 = customerIds[2];

        // Draft: created, items added, never confirmed — no stock touched.
        await EnsureDraftOrderAsync(
            accountId, customer1, today.AddDays(-4), mediumProductId, DraftOrderQuantityEggs, ct);
        await EnsureDraftOrderAsync(
            accountId, customer2, today.AddDays(-3), mediumProductId, DraftOrderQuantityEggs, ct);

        // Confirmed (unpaid): same product/grade as every other confirmed
        // order below — see ConfirmedOrderQuantityEggs for why that matters.
        await EnsureConfirmedOrderAsync(
            accountId, customer1, today.AddDays(-6), largeProductId, ConfirmedOrderQuantityEggs, ct);
        await EnsureConfirmedOrderAsync(
            accountId, customer2, today.AddDays(-5), largeProductId, ConfirmedOrderQuantityEggs, ct);

        // Confirmed + partially paid — also left un-voided, so it doubles as
        // the "voidable confirmed order" a later hazard pass can void.
        var partialOrderId = await EnsureConfirmedOrderAsync(
            accountId, customer3, today.AddDays(-2), largeProductId, ConfirmedOrderQuantityEggs, ct);
        await EnsurePartialPaymentAsync(accountId, partialOrderId, today.AddDays(-1), ct);

        await SeedRecurringOrdersAsync(accountId, today, customerIds, largeProductId, sim, ct);
    }

    // Spreads a modest, deterministic drip of additional confirmed orders
    // across the full history window — see RecurringStartDay/RecurringCadenceDays
    // above. Always the Large product: keeps it inside the same FIFO
    // shared-old-lot-pool shape ConfirmedOrderQuantityEggs already documents,
    // and every date here is <= EffectiveHistoryDays, so production history
    // (seeded before sales) always has a lot on or before that date to
    // allocate from.
    private async Task SeedRecurringOrdersAsync(
        Guid accountId, DateOnly today, IReadOnlyList<Guid> customerIds, Guid largeProductId,
        SimulationOptions sim, CancellationToken ct)
    {
        var historyDays = EffectiveHistoryDays(sim);
        var i = 0;
        for (var d = RecurringStartDay; d <= historyDays; d += RecurringCadenceDays, i++)
        {
            var customerId = customerIds[i % customerIds.Count];
            await EnsureConfirmedOrderAsync(
                accountId, customerId, today.AddDays(-d), largeProductId, RecurringOrderQuantityEggs, ct);
        }
    }

    private async Task<IReadOnlyDictionary<string, Guid>> SeedProductCatalogAsync(
        Guid accountId, IReadOnlyDictionary<string, Guid> grades, CancellationToken ct)
    {
        var products = new Dictionary<string, Guid>();
        foreach (var (gradeName, productName, price) in CatalogWanted)
        {
            if (!grades.TryGetValue(gradeName, out var gradeId))
                throw new InvalidOperationException(
                    $"Simulation seed needs the '{gradeName}' egg grade for the sales catalog.");
            products[gradeName] = await EnsureProductAsync(accountId, productName, gradeId, price, ct);
        }
        return products;
    }

    private async Task<Guid> EnsureProductAsync(
        Guid accountId, string name, Guid eggGradeId, long priceMinorUnits, CancellationToken ct)
    {
        var existing = await db.Products.FirstOrDefaultAsync(p => p.Name == name, ct);
        if (existing is not null) return existing.Id;

        var result = await createProduct.HandleAsync(new CreateProductCommand(
            name, "Egg", "Egg", priceMinorUnits, eggGradeId, "Simulation fixture product"), accountId, ct);
        Require(result, $"create product {name}");
        return result.Value;
    }

    private async Task<IReadOnlyList<Guid>> SeedCustomersAsync(Guid accountId, CancellationToken ct)
    {
        var ids = new List<Guid>();
        foreach (var (name, phone, note) in CustomersWanted)
            ids.Add(await EnsureCustomerAsync(accountId, name, phone, note, ct));
        return ids;
    }

    private async Task<Guid> EnsureCustomerAsync(
        Guid accountId, string name, string phone, string note, CancellationToken ct)
    {
        var existing = await db.Customers.FirstOrDefaultAsync(c => c.Name == name, ct);
        if (existing is not null) return existing.Id;

        var result = await createCustomer.HandleAsync(
            new CreateCustomerCommand(name, phone, Note: note), accountId, ct);
        Require(result, $"create customer {name}");
        return result.Value;
    }

    // (CustomerId, OrderDate) is the natural key THIS seeder controls (every
    // call site below uses a distinct date per customer) — SalesOrder itself
    // has no other stable, human-chosen identity to check idempotency against
    // (ReferenceNumber is minted from a random order id inside the handler).
    private async Task<Guid?> FindOrderAsync(Guid customerId, DateOnly orderDate, CancellationToken ct) =>
        (await db.SalesOrders
            .FirstOrDefaultAsync(o => o.CustomerId == customerId && o.OrderDate == orderDate, ct))?.Id;

    private async Task<Guid> EnsureDraftOrderAsync(
        Guid accountId, Guid customerId, DateOnly orderDate, Guid productId, int quantityEggs,
        CancellationToken ct)
    {
        var existing = await FindOrderAsync(customerId, orderDate, ct);
        if (existing is not null) return existing.Value;

        var created = await createSalesOrder.HandleAsync(
            new CreateSalesOrderCommand(customerId, orderDate), accountId, ct);
        Require(created, $"create sales order for customer {customerId} on {orderDate:yyyy-MM-dd}");
        var orderId = created.Value;

        // Unit/price both null: defaults from the product (Egg unit, the
        // catalog price seeded above) — same pattern as DemoDataSeeder.
        var added = await addOrderItem.HandleAsync(
            new AddOrderItemCommand(orderId, productId, quantityEggs, null, null), accountId, ct);
        Require(added, $"add item to sales order {orderId}");

        return orderId;
    }

    private async Task<Guid> EnsureConfirmedOrderAsync(
        Guid accountId, Guid customerId, DateOnly orderDate, Guid productId, int quantityEggs,
        CancellationToken ct)
    {
        var orderId = await EnsureDraftOrderAsync(accountId, customerId, orderDate, productId, quantityEggs, ct);

        // Status, not "the order already existed", decides whether to
        // confirm — an order that exists but is still Draft still needs
        // confirming on a re-run.
        var order = await db.SalesOrders.FirstAsync(o => o.Id == orderId, ct);
        if (order.Status == SalesOrderStatus.Confirmed) return orderId;

        // FIFO allocation (tech spec §10.9.1): draws from the oldest
        // available lots for this grade under a FOR UPDATE lock — this is
        // what depletes the production history's egg lots.
        var confirmed = await confirmSale.HandleAsync(new ConfirmSaleCommand(orderId), accountId, ct);
        Require(confirmed, $"confirm sales order {orderId}");
        return orderId;
    }

    private async Task EnsurePartialPaymentAsync(
        Guid accountId, Guid orderId, DateOnly paymentDate, CancellationToken ct)
    {
        var hasPayment = await db.Payments.AnyAsync(p => p.SalesOrderId == orderId, ct);
        if (hasPayment) return;

        var order = await db.SalesOrders.FirstAsync(o => o.Id == orderId, ct);
        // Half the total, rounded down — strictly less than TotalAmount so
        // the order stays genuinely partially paid, never fully settled.
        var amount = order.TotalAmount.MinorUnits / 2;
        if (amount <= 0)
            throw new InvalidOperationException(
                $"Simulation seed: sales order {orderId}'s total is too small to seed a partial payment.");

        var result = await recordPayment.HandleAsync(new RecordPaymentCommand(
            orderId, paymentDate, amount, "Cash", null, "Simulation fixture partial payment"), accountId, ct);
        Require(result, $"record partial payment for sales order {orderId}");
    }

    // --- Inventory: items, opening purchases, adjustments, feed/water usage,
    // expenses (#243 Task 3c) --------------------------------------------
    //
    // Same real-handler-reuse discipline as sales above (CreateInventoryItem/
    // RecordPurchase/RecordAdjustment/RecordFeedUsage/RecordWaterUsage/
    // CreateExpenseCategory/CreateExpense) so reports/exports built against
    // this data see the same shapes a user clicking through Inventory/
    // Expenses would produce. Idempotency: items/categories by Name
    // (EnsureFlockAsync-style); the opening purchase by "does the item
    // already have a lot" (HasLotsAsync — this seeder only ever creates ONE
    // opening lot per item); the adjustment by "does the lot already carry a
    // correction movement"; usage/expense rows by the (flock/item, date) or
    // Description natural key this seeder controls, same convention as
    // SeedFlockHistoryAsync's natural-key skip and FindOrderAsync above.

    private static readonly (string Name, string Category, string Unit, long DefaultUnitCostMinorUnits)[]
        InventoryItemsWanted =
        [
            ("Sim Layer Feed", "Feed", "kg", 42),
            ("Sim Pine Shavings", "Bedding", "bags", 850),
        ];

    private const decimal FeedOpeningPurchaseQuantity = 3000m; // kg
    private const decimal BeddingOpeningPurchaseQuantity = 120m; // bags
    private const decimal FeedDiscardQuantity = -15m; // kg — spoilage write-off
    private const decimal BeddingAdjustmentQuantity = -6m; // bags — recount shrinkage

    // Modest relative to the opening purchase (net of the discard above) —
    // total feed usage across every flock/day stays far inside what's on
    // hand, so the seed never fails closed on insufficient stock regardless
    // of how many flocks are configured.
    private const decimal FeedUsagePerFlockPerDay = 18m; // kg
    private const int FeedUsageDays = 4;

    private const decimal WaterUsagePerFlockPerDay = 250m; // L
    private const int WaterUsageDays = 4;

    private async Task SeedInventoryOperationsAsync(
        Guid accountId, DateOnly today, IReadOnlyList<Guid> flockIds, SimulationOptions sim, CancellationToken ct)
    {
        // Received well before any usage date so FIFO always finds it on-hand
        // as-of any usage day — same "older than everything it must cover"
        // shape as the flock placement dates in SeedFlockTopologyAsync.
        var openingDate = today.AddDays(-(EffectiveHistoryDays(sim) + 5));

        var itemIds = await SeedInventoryItemsAsync(accountId, ct);
        var feedItemId = itemIds["Sim Layer Feed"];
        var beddingItemId = itemIds["Sim Pine Shavings"];

        var feedLotId = await EnsureOpeningPurchaseAsync(
            accountId, feedItemId, openingDate, FeedOpeningPurchaseQuantity, ct);
        var beddingLotId = await EnsureOpeningPurchaseAsync(
            accountId, beddingItemId, openingDate, BeddingOpeningPurchaseQuantity, ct);

        var adjustmentDate = openingDate.AddDays(2);
        await EnsureAdjustmentAsync(
            accountId, feedItemId, feedLotId, adjustmentDate, "Discard",
            FeedDiscardQuantity, "Simulation fixture: torn bag spoiled in storage", ct);
        await EnsureAdjustmentAsync(
            accountId, beddingItemId, beddingLotId, adjustmentDate, "Adjustment",
            BeddingAdjustmentQuantity, "Simulation fixture: recount shrinkage", ct);

        await SeedFeedUsageAsync(accountId, today, flockIds, feedItemId, ct);
        await SeedWaterUsageAsync(accountId, today, flockIds, ct);
        await SeedExpensesAsync(accountId, today, flockIds, sim, ct);
    }

    private async Task<IReadOnlyDictionary<string, Guid>> SeedInventoryItemsAsync(Guid accountId, CancellationToken ct)
    {
        var itemIds = new Dictionary<string, Guid>();
        foreach (var (name, category, unit, cost) in InventoryItemsWanted)
            itemIds[name] = await EnsureInventoryItemAsync(accountId, name, category, unit, cost, ct);
        return itemIds;
    }

    private async Task<Guid> EnsureInventoryItemAsync(
        Guid accountId, string name, string category, string unit, long defaultUnitCostMinorUnits,
        CancellationToken ct)
    {
        var existing = await db.InventoryItems.FirstOrDefaultAsync(i => i.Name == name, ct);
        if (existing is not null) return existing.Id;

        var result = await createInventoryItem.HandleAsync(
            new CreateInventoryItemCommand(name, category, unit, defaultUnitCostMinorUnits), accountId, ct);
        Require(result, $"create inventory item {name}");
        return result.Value;
    }

    private async Task<Guid> EnsureOpeningPurchaseAsync(
        Guid accountId, Guid itemId, DateOnly receivedDate, decimal quantity, CancellationToken ct)
    {
        // HasLotsAsync doubles as the idempotency probe: this seeder only
        // ever creates ONE opening lot per item, so "any lot exists" is
        // equivalent to "the opening purchase already ran".
        if (await inventoryItems.HasLotsAsync(itemId, ct))
            return (await db.InventoryLots.FirstAsync(l => l.InventoryItemId == itemId, ct)).Id;

        // UnitCostMinorUnits omitted: falls back to the item's default cost
        // set above.
        var result = await recordPurchase.HandleAsync(new RecordPurchaseCommand(
            itemId, receivedDate, quantity, UnitCostMinorUnits: null, LotNumber: "SIM-OPEN-1",
            ExpiryDate: null, Note: "Simulation fixture opening stock"), accountId, ct);
        Require(result, $"record opening purchase for inventory item {itemId}");
        return result.Value;
    }

    private async Task EnsureAdjustmentAsync(
        Guid accountId, Guid itemId, Guid lotId, DateOnly date, string type, decimal quantityDelta, string reason,
        CancellationToken ct)
    {
        var hasAdjustment = await db.InventoryMovements.AnyAsync(
            m => m.InventoryLotId == lotId
                 && (m.Type == InventoryMovementType.Adjustment || m.Type == InventoryMovementType.Discard), ct);
        if (hasAdjustment) return;

        var result = await recordAdjustment.HandleAsync(new RecordAdjustmentCommand(
            itemId, lotId, date, type, quantityDelta, reason), accountId, ct);
        Require(result, $"record inventory adjustment for lot {lotId}");
    }

    private async Task SeedFeedUsageAsync(
        Guid accountId, DateOnly today, IReadOnlyList<Guid> flockIds, Guid feedItemId, CancellationToken ct)
    {
        foreach (var flockId in flockIds)
            for (var d = 1; d <= FeedUsageDays; d++)
            {
                // Day 1+ past, matching the daily-entry-history convention
                // above (day-of-anchor could spuriously read as "future"
                // against the farm's own, timezone-skewed today).
                var date = today.AddDays(-d);
                var exists = await db.FeedUsages.AnyAsync(
                    u => u.FlockId == flockId && u.InventoryItemId == feedItemId && u.Date == date, ct);
                if (exists) continue;

                var result = await recordFeedUsage.HandleAsync(new RecordFeedUsageCommand(
                    flockId, feedItemId, date, FeedUsagePerFlockPerDay,
                    "Simulation fixture daily feeding"), accountId, ct);
                Require(result, $"record feed usage for flock {flockId} on {date:yyyy-MM-dd}");
            }
    }

    private async Task SeedWaterUsageAsync(
        Guid accountId, DateOnly today, IReadOnlyList<Guid> flockIds, CancellationToken ct)
    {
        foreach (var flockId in flockIds)
            for (var d = 1; d <= WaterUsageDays; d++)
            {
                var date = today.AddDays(-d);
                var exists = await db.WaterUsages.AnyAsync(u => u.FlockId == flockId && u.Date == date, ct);
                if (exists) continue;

                var result = await recordWaterUsage.HandleAsync(new RecordWaterUsageCommand(
                    flockId, date, WaterUsagePerFlockPerDay, Unit: "L", Source: "Well",
                    MeterStart: null, MeterEnd: null, Note: "Simulation fixture daily water"), accountId, ct);
                Require(result, $"record water usage for flock {flockId} on {date:yyyy-MM-dd}");
            }
    }

    private static readonly string[] ExpenseCategoriesWanted = ["Sim Utilities", "Sim Repairs & Maintenance"];

    // Deterministic recurring expense amount — see RecurringStartDay/
    // RecurringCadenceDays above for why this exists (same thin-tail problem
    // as sales, same fix).
    private const long RecurringExpenseAmountMinorUnits = 4_000;

    private async Task SeedExpensesAsync(
        Guid accountId, DateOnly today, IReadOnlyList<Guid> flockIds, SimulationOptions sim, CancellationToken ct)
    {
        var categoryIds = await SeedExpenseCategoriesAsync(accountId, ct);

        // flockIds[1] is safe here: RestrictOneWorkerAsync already throws if
        // fewer than 2 flocks exist, and it runs before this step.
        (string Category, int DaysAgo, string Description, long AmountMinorUnits, Guid? FlockId)[] wanted =
        [
            ("Sim Utilities", 7, "Sim Electricity Bill", 15_000, null),
            ("Sim Utilities", 3, "Sim Water Utility Bill", 8_000, null),
            ("Sim Repairs & Maintenance", 5, "Sim Coop Roof Repair", 42_000, flockIds[0]),
            ("Sim Repairs & Maintenance", 2, "Sim Feeder Replacement Part", 6_500, flockIds[1]),
        ];

        foreach (var (category, daysAgo, description, amount, flockId) in wanted)
            await EnsureExpenseAsync(
                accountId, categoryIds[category], today.AddDays(-daysAgo), description, amount, flockId, ct);

        await SeedRecurringExpensesAsync(accountId, today, flockIds, categoryIds, sim, ct);
    }

    // Spreads a modest, deterministic drip of additional expenses across the
    // full history window — mirrors SeedRecurringOrdersAsync. Category and
    // flock attribution alternate so both categories and both flock-attributed
    // / farm-wide expenses keep growing as HistoryDays grows, not just the
    // count.
    private async Task SeedRecurringExpensesAsync(
        Guid accountId, DateOnly today, IReadOnlyList<Guid> flockIds,
        IReadOnlyDictionary<string, Guid> categoryIds, SimulationOptions sim, CancellationToken ct)
    {
        var historyDays = EffectiveHistoryDays(sim);
        var i = 0;
        for (var d = RecurringStartDay; d <= historyDays; d += RecurringCadenceDays, i++)
        {
            var category = i % 2 == 0 ? "Sim Utilities" : "Sim Repairs & Maintenance";
            var flockId = i % 2 == 0 ? (Guid?)null : flockIds[i % flockIds.Count];
            var description = $"Sim Recurring Expense Day {d}";
            await EnsureExpenseAsync(
                accountId, categoryIds[category], today.AddDays(-d), description,
                RecurringExpenseAmountMinorUnits, flockId, ct);
        }
    }

    private async Task<IReadOnlyDictionary<string, Guid>> SeedExpenseCategoriesAsync(
        Guid accountId, CancellationToken ct)
    {
        var categoryIds = new Dictionary<string, Guid>();
        foreach (var name in ExpenseCategoriesWanted)
            categoryIds[name] = await EnsureExpenseCategoryAsync(accountId, name, ct);
        return categoryIds;
    }

    private async Task<Guid> EnsureExpenseCategoryAsync(Guid accountId, string name, CancellationToken ct)
    {
        var existing = await db.ExpenseCategories.FirstOrDefaultAsync(c => c.Name == name, ct);
        if (existing is not null) return existing.Id;

        var result = await createExpenseCategory.HandleAsync(new CreateExpenseCategoryCommand(name), accountId, ct);
        Require(result, $"create expense category {name}");
        return result.Value;
    }

    private async Task EnsureExpenseAsync(
        Guid accountId, Guid categoryId, DateOnly date, string description, long amountMinorUnits, Guid? flockId,
        CancellationToken ct)
    {
        // Description is the natural key THIS seeder controls (every entry
        // above is a distinct fixture string) — same convention as
        // FindOrderAsync's (CustomerId, OrderDate) above.
        var exists = await db.Expenses.AnyAsync(e => e.Description == description, ct);
        if (exists) return;

        var result = await createExpense.HandleAsync(new CreateExpenseCommand(
            categoryId, date, description, amountMinorUnits, flockId,
            Note: "Simulation fixture expense"), accountId, ct);
        Require(result, $"create expense {description}");
    }

    // --- Second, pristine account (tenant-isolation fixture) -----------

    private async Task SeedSecondAccountAsync(CancellationToken ct)
    {
        // The account query filter would hide a second tenant's row —
        // IgnoreQueryFilters to see it regardless of which tenant is resolved.
        var exists = await db.Accounts.IgnoreQueryFilters().AnyAsync(a => a.Id == SecondAccountId, ct);
        if (exists) return;

        db.Accounts.Add(Account.Create(SecondAccountId, "Simulation Second Farm", "UTC", "USD"));
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Seeded second pristine simulation account {AccountId}.", SecondAccountId);
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
    // #279 review Fix 2 (Agent A + pi + codex, consensus): every count below
    // is asserted EXACTLY, not as a ">= 1" floor — including the
    // Draft/Submitted/Locked lifecycle split (Locked is derived by mirroring
    // DailyEntryLockSweep's own cutoff — see ExpectedLockedEntryCount below —
    // rather than left unchecked; Draft/Submitted then reconcile against the
    // total). A bug that drops all-but-one row of any seeder-controlled band
    // now fails ValidateCounts instead of still certifying complete: true.

    public const int ManifestSchemaVersion = 1;

    // Mirrors SeedFlockTopologyAsync's `wanted` array length. Kept as an
    // independent constant (not read off that array) so ComputeExpectedCounts
    // needs no DB round-trip to state what the seed intends to produce — it
    // still takes `today`/`farmToday` as plain DateOnly inputs (#279 Fix 2,
    // for the exact Locked/Submitted split below) but is otherwise pure.
    private const int FlockTopologyCount = 2;

    // Mirrors SeedFlockHistoryAsync's per-entry grade-quantity array literal
    // (Large/Medium/Small): SubmitDailyEntryHandler mints exactly one egg lot
    // per grade line, and the deterministic baseline/cracked/dirty/discarded
    // math in SeedFlockHistoryAsync always leaves all three grade buckets > 0
    // for every configured baseline — so every SUBMITTED entry (never a
    // still-Draft one; see DraftWindowDays) produces exactly this many lots,
    // not merely "at least one" (#279 Fix 2).
    private const int GradesPerDailyEntry = 3;

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

    private async Task<SimulationManifest> EmitManifestAsync(
        Guid accountId, DateOnly today, SimulationOptions sim, CancellationToken ct)
    {
        var (counts, states) = await ComputeCountsAsync(accountId, ct);
        // Same farm-local "today" lookup DailyEntryLockSweep itself just used
        // (lockSweep.RunAsync already ran, above, in SeedAsync) — recomputing
        // it here mirrors the sweep's OWN cutoff derivation exactly instead
        // of approximating it, at the cost of the same vanishingly small
        // UTC-midnight-mid-SeedAsync race the class header already documents
        // for MinSentinelAgeDays.
        var farmToday = clock.TodayInZone(sim.TimeZoneId);
        var expected = ComputeExpectedCounts(sim, today, farmToday);

        // Fail-closed: throws on ANY shortfall — a partial/short seed must
        // fail startup, not publish a "complete" manifest.
        ValidateCounts(counts, states, expected);

        var fingerprint = ComputeFingerprint(sim.Seed, counts, states);
        var manifest = new SimulationManifest(
            SchemaVersion: ManifestSchemaVersion,
            Seed: sim.Seed,
            HistoryDays: sim.HistoryDays,
            GeneratedAtAnchor: today,
            Counts: counts,
            LifecycleStates: states,
            Complete: true,
            Fingerprint: fingerprint);

        if (!string.IsNullOrWhiteSpace(sim.CredentialOutputPath))
            await WriteManifestFileAsync(sim.CredentialOutputPath, manifest, ct);

        logger.LogInformation(
            "Simulation seed manifest complete (fingerprint {Fingerprint}).", fingerprint);

        return manifest;
    }

    // Everything below except Users/Accounts already sits behind AppDbContext's
    // tenant query filter (OnModelCreating), and SeedAsync resolved the
    // tenant to accountId at the top — so these queries are already scoped to
    // the primary account without an explicit .Where(AccountId == accountId).
    // ApplicationUser carries no tenant filter (it's Identity's own table,
    // not one of ours), so users are filtered explicitly. Account carries a
    // filter too, but IgnoreQueryFilters is used deliberately — the manifest
    // needs to see the second, pristine tenant fixture as well (mirrors
    // SeedSecondAccountAsync's own IgnoreQueryFilters use).
    private async Task<(SimulationManifestCounts Counts, SimulationLifecycleStates States)> ComputeCountsAsync(
        Guid accountId, CancellationToken ct)
    {
        var usersTotal = await db.Users.CountAsync(u => u.AccountId == accountId, ct);
        var ownerCount = (await users.GetUsersInRoleAsync(Roles.Owner)).Count(u => u.AccountId == accountId);
        var managerCount = (await users.GetUsersInRoleAsync(Roles.Manager)).Count(u => u.AccountId == accountId);
        var salesCount = (await users.GetUsersInRoleAsync(Roles.Sales)).Count(u => u.AccountId == accountId);
        var readOnlyCount = (await users.GetUsersInRoleAsync(Roles.ReadOnly)).Count(u => u.AccountId == accountId);
        // Workers deliberately carry no role row (SeedCastAsync) — derive by
        // subtraction rather than a role lookup that would always find none.
        var workerCount = usersTotal - ownerCount - managerCount - salesCount - readOnlyCount;

        var accountCount = await db.Accounts.IgnoreQueryFilters().CountAsync(ct);
        var flockCount = await db.Flocks.CountAsync(ct);

        var dailyEntriesTotal = await db.DailyEntries.CountAsync(ct);
        var draftEntries = await db.DailyEntries.CountAsync(e => e.Status == DailyEntryStatus.Draft, ct);
        var submittedEntries = await db.DailyEntries.CountAsync(e => e.Status == DailyEntryStatus.Submitted, ct);
        var lockedEntries = await db.DailyEntries.CountAsync(e => e.Status == DailyEntryStatus.Locked, ct);

        var eggLotCount = await db.EggLots.CountAsync(ct);

        var salesOrdersTotal = await db.SalesOrders.CountAsync(ct);
        var draftOrders = await db.SalesOrders.CountAsync(o => o.Status == SalesOrderStatus.Draft, ct);
        var confirmedOrders = await db.SalesOrders.CountAsync(o => o.Status == SalesOrderStatus.Confirmed, ct);
        var shippedOrders = await db.SalesOrders.CountAsync(o => o.Status == SalesOrderStatus.Shipped, ct);
        var invoicedOrders = await db.SalesOrders.CountAsync(o => o.Status == SalesOrderStatus.Invoiced, ct);
        var cancelledOrders = await db.SalesOrders.CountAsync(o => o.Status == SalesOrderStatus.Cancelled, ct);
        var voidedOrders = await db.SalesOrders.CountAsync(o => o.Status == SalesOrderStatus.Voided, ct);

        var paymentCount = await db.Payments.CountAsync(ct);

        var inventoryItemCount = await db.InventoryItems.CountAsync(ct);
        var inventoryLotCount = await db.InventoryLots.CountAsync(ct);

        var movementsTotal = await db.InventoryMovements.CountAsync(ct);
        var purchaseMovements = await db.InventoryMovements.CountAsync(
            m => m.Type == InventoryMovementType.Purchase, ct);
        var usageMovements = await db.InventoryMovements.CountAsync(
            m => m.Type == InventoryMovementType.Usage, ct);
        var adjustmentMovements = await db.InventoryMovements.CountAsync(
            m => m.Type == InventoryMovementType.Adjustment, ct);
        var discardMovements = await db.InventoryMovements.CountAsync(
            m => m.Type == InventoryMovementType.Discard, ct);

        var feedUsageCount = await db.FeedUsages.CountAsync(ct);
        var waterUsageCount = await db.WaterUsages.CountAsync(ct);

        var expenseCategoryCount = await db.ExpenseCategories.CountAsync(ct);
        var expenseCount = await db.Expenses.CountAsync(ct);

        var counts = new SimulationManifestCounts(
            Accounts: accountCount,
            Owners: ownerCount,
            Managers: managerCount,
            Sales: salesCount,
            Workers: workerCount,
            ReadOnly: readOnlyCount,
            UsersTotal: usersTotal,
            Flocks: flockCount,
            DailyEntriesTotal: dailyEntriesTotal,
            EggLots: eggLotCount,
            SalesOrdersTotal: salesOrdersTotal,
            Payments: paymentCount,
            InventoryItems: inventoryItemCount,
            InventoryLots: inventoryLotCount,
            InventoryMovementsTotal: movementsTotal,
            FeedUsageRows: feedUsageCount,
            WaterUsageRows: waterUsageCount,
            ExpenseCategories: expenseCategoryCount,
            Expenses: expenseCount);

        var states = new SimulationLifecycleStates(
            DailyEntries: new SimulationDailyEntryStates(draftEntries, submittedEntries, lockedEntries),
            SalesOrders: new SimulationSalesOrderStates(
                draftOrders, confirmedOrders, shippedOrders, invoicedOrders, cancelledOrders, voidedOrders),
            InventoryMovements: new SimulationInventoryMovementStates(
                purchaseMovements, usageMovements, adjustmentMovements, discardMovements));

        return (counts, states);
    }

    // Pure function of SimulationOptions plus the two plain DateOnly anchors
    // (today, farmToday) — still no DB access, so a unit test can call it
    // with hand-built inputs too. See the class-level comment above
    // ValidateCounts for why every field here is DERIVED rather than a
    // hardcoded depth-specific number.
    private static SimulationExpectedCounts ComputeExpectedCounts(SimulationOptions sim, DateOnly today, DateOnly farmToday)
    {
        var historyDays = EffectiveHistoryDays(sim);
        var recurringPoints = RecurringPointCount(historyDays);

        var dailyEntriesTotal = FlockTopologyCount * historyDays;
        var draftEntries = FlockTopologyCount * DraftWindowDays;
        var lockedEntries = ExpectedLockedEntryCount(today, farmToday, historyDays);
        var submittedEntries = dailyEntriesTotal - draftEntries - lockedEntries;
        // Entries that ever reached Submitted (whether still Submitted or
        // since moved to Locked) — the population SubmitDailyEntryHandler
        // actually minted egg lots for; Draft entries never call it.
        var submittedOrLockedEntries = dailyEntriesTotal - draftEntries;

        var salesOrdersConfirmed = LifecycleConfirmedOrdersCount + recurringPoints;
        var feedUsageRows = FlockTopologyCount * FeedUsageDays;
        // One Purchase + one Adjustment/Discard movement per inventory item,
        // plus one Usage movement per feed usage row — EnsureOpeningPurchaseAsync
        // creates exactly one lot per item, so FIFO in RecordFeedUsageHandler
        // never needs to split a single usage across more than one lot.
        var inventoryMovementsTotal =
            InventoryItemsWanted.Length + InventoryItemsWanted.Length + feedUsageRows;

        return new SimulationExpectedCounts(
            Accounts: 2, // primary + SecondAccountId — never a third on a re-run.
            Owners: 1, // the reused seeded admin — this seeder never creates a second Owner.
            Managers: sim.Managers,
            Sales: sim.Sales,
            Workers: sim.Workers,
            ReadOnly: sim.ReadOnly,
            UsersTotal: 1 + sim.Managers + sim.Sales + sim.Workers + sim.ReadOnly,
            Flocks: FlockTopologyCount,
            DailyEntriesTotal: dailyEntriesTotal,
            DraftEntries: draftEntries,
            SubmittedEntries: submittedEntries,
            LockedEntries: lockedEntries,
            EggLots: GradesPerDailyEntry * submittedOrLockedEntries,
            SalesOrdersTotal: DraftOrdersCount + salesOrdersConfirmed,
            SalesOrdersDraft: DraftOrdersCount,
            SalesOrdersConfirmed: salesOrdersConfirmed,
            Payments: 1, // exactly one partial payment (EnsurePartialPaymentAsync).
            InventoryItems: InventoryItemsWanted.Length,
            InventoryLots: InventoryItemsWanted.Length, // one opening lot per item.
            InventoryPurchaseMovements: InventoryItemsWanted.Length,
            InventoryAdjustmentOrDiscardMovements: InventoryItemsWanted.Length, // one each.
            InventoryUsageMovements: feedUsageRows,
            InventoryMovementsTotal: inventoryMovementsTotal,
            FeedUsageRows: feedUsageRows,
            WaterUsageRows: FlockTopologyCount * WaterUsageDays,
            ExpenseCategories: ExpenseCategoriesWanted.Length,
            Expenses: LifecycleExpensesCount + recurringPoints);
    }

    // Pure, no DB access — a test can hand this a synthetic short
    // SimulationManifestCounts/SimulationLifecycleStates and assert it throws,
    // proving the fail-closed path without needing to sabotage a real seed run.
    // The fail-closed gate, run AFTER the lock sweep: asserts every count EXACTLY
    // (including the Draft/Submitted/Locked split) and throws on any shortfall,
    // collecting every mismatch so one failed run reports the WHOLE shortfall.
    // (#279: the "already seeded?" question is answered by the durable
    // SimulationSeedState.CompletedAtUtc marker, NOT by a tolerant count probe,
    // so this method has a single exact mode again.)
    internal static void ValidateCounts(
        SimulationManifestCounts counts, SimulationLifecycleStates states, SimulationExpectedCounts expected)
    {
        var failures = new List<string>();
        void Check(bool ok, string message)
        {
            if (!ok) failures.Add(message);
        }

        Check(counts.Accounts == expected.Accounts,
            $"accounts: expected exactly {expected.Accounts}, got {counts.Accounts}");
        Check(counts.Owners == expected.Owners, $"users.owners: expected {expected.Owners}, got {counts.Owners}");
        Check(counts.Managers == expected.Managers,
            $"users.managers: expected {expected.Managers}, got {counts.Managers}");
        Check(counts.Sales == expected.Sales, $"users.sales: expected {expected.Sales}, got {counts.Sales}");
        Check(counts.Workers == expected.Workers, $"users.workers: expected {expected.Workers}, got {counts.Workers}");
        Check(counts.ReadOnly == expected.ReadOnly,
            $"users.readOnly: expected {expected.ReadOnly}, got {counts.ReadOnly}");
        Check(counts.UsersTotal == expected.UsersTotal,
            $"users.total: expected {expected.UsersTotal}, got {counts.UsersTotal}");
        Check(counts.Flocks == expected.Flocks, $"flocks: expected {expected.Flocks}, got {counts.Flocks}");
        Check(counts.DailyEntriesTotal == expected.DailyEntriesTotal,
            $"dailyEntries.total: expected {expected.DailyEntriesTotal}, got {counts.DailyEntriesTotal}");
        Check(states.DailyEntries.Draft == expected.DraftEntries,
            $"dailyEntries.draft: expected {expected.DraftEntries}, got {states.DailyEntries.Draft}");
        Check(states.DailyEntries.Submitted == expected.SubmittedEntries,
            $"dailyEntries.submitted: expected {expected.SubmittedEntries}, got {states.DailyEntries.Submitted}");
        Check(states.DailyEntries.Locked == expected.LockedEntries,
            $"dailyEntries.locked: expected {expected.LockedEntries}, got {states.DailyEntries.Locked}");
        var dailyEntriesSum = states.DailyEntries.Draft + states.DailyEntries.Submitted + states.DailyEntries.Locked;
        Check(dailyEntriesSum == counts.DailyEntriesTotal,
            $"dailyEntries reconciliation: draft+submitted+locked ({dailyEntriesSum}) != total ({counts.DailyEntriesTotal})");
        Check(counts.EggLots == expected.EggLots, $"eggLots: expected {expected.EggLots}, got {counts.EggLots}");
        Check(counts.SalesOrdersTotal == expected.SalesOrdersTotal,
            $"salesOrders.total: expected {expected.SalesOrdersTotal}, got {counts.SalesOrdersTotal}");
        Check(states.SalesOrders.Draft == expected.SalesOrdersDraft,
            $"salesOrders.draft: expected {expected.SalesOrdersDraft}, got {states.SalesOrders.Draft}");
        Check(states.SalesOrders.Confirmed == expected.SalesOrdersConfirmed,
            $"salesOrders.confirmed: expected {expected.SalesOrdersConfirmed}, got {states.SalesOrders.Confirmed}");
        // The seeder never ships/invoices/cancels/voids anything it creates
        // (SeedSalesAsync's own header comment) — these terminal states must
        // stay at exactly zero, not merely unchecked.
        Check(states.SalesOrders.Shipped == 0, $"salesOrders.shipped: expected 0, got {states.SalesOrders.Shipped}");
        Check(states.SalesOrders.Invoiced == 0, $"salesOrders.invoiced: expected 0, got {states.SalesOrders.Invoiced}");
        Check(states.SalesOrders.Cancelled == 0, $"salesOrders.cancelled: expected 0, got {states.SalesOrders.Cancelled}");
        Check(states.SalesOrders.Voided == 0, $"salesOrders.voided: expected 0, got {states.SalesOrders.Voided}");
        var salesOrdersSum = states.SalesOrders.Draft + states.SalesOrders.Confirmed + states.SalesOrders.Shipped
            + states.SalesOrders.Invoiced + states.SalesOrders.Cancelled + states.SalesOrders.Voided;
        Check(salesOrdersSum == counts.SalesOrdersTotal,
            $"salesOrders reconciliation: sum of states ({salesOrdersSum}) != total ({counts.SalesOrdersTotal})");
        Check(counts.Payments == expected.Payments, $"payments: expected {expected.Payments}, got {counts.Payments}");
        Check(counts.InventoryItems == expected.InventoryItems,
            $"inventoryItems: expected {expected.InventoryItems}, got {counts.InventoryItems}");
        Check(counts.InventoryLots == expected.InventoryLots,
            $"inventoryLots: expected {expected.InventoryLots}, got {counts.InventoryLots}");
        Check(states.InventoryMovements.Purchase == expected.InventoryPurchaseMovements,
            $"inventoryMovements.purchase: expected {expected.InventoryPurchaseMovements}, got {states.InventoryMovements.Purchase}");
        Check(states.InventoryMovements.Adjustment + states.InventoryMovements.Discard
              == expected.InventoryAdjustmentOrDiscardMovements,
            $"inventoryMovements.adjustment+discard: expected {expected.InventoryAdjustmentOrDiscardMovements}, " +
            $"got {states.InventoryMovements.Adjustment + states.InventoryMovements.Discard}");
        Check(states.InventoryMovements.Usage == expected.InventoryUsageMovements,
            $"inventoryMovements.usage: expected {expected.InventoryUsageMovements}, got {states.InventoryMovements.Usage}");
        Check(counts.InventoryMovementsTotal == expected.InventoryMovementsTotal,
            $"inventoryMovements.total: expected {expected.InventoryMovementsTotal}, got {counts.InventoryMovementsTotal}");
        var inventoryMovementsSum = states.InventoryMovements.Purchase + states.InventoryMovements.Usage
            + states.InventoryMovements.Adjustment + states.InventoryMovements.Discard;
        Check(inventoryMovementsSum == counts.InventoryMovementsTotal,
            $"inventoryMovements reconciliation: sum of states ({inventoryMovementsSum}) != total ({counts.InventoryMovementsTotal})");
        Check(counts.FeedUsageRows == expected.FeedUsageRows,
            $"feedUsageRows: expected {expected.FeedUsageRows}, got {counts.FeedUsageRows}");
        Check(counts.WaterUsageRows == expected.WaterUsageRows,
            $"waterUsageRows: expected {expected.WaterUsageRows}, got {counts.WaterUsageRows}");
        Check(counts.ExpenseCategories == expected.ExpenseCategories,
            $"expenseCategories: expected {expected.ExpenseCategories}, got {counts.ExpenseCategories}");
        Check(counts.Expenses == expected.Expenses, $"expenses: expected {expected.Expenses}, got {counts.Expenses}");

        if (failures.Count > 0)
            throw new InvalidOperationException(
                "Simulation seed completion check failed — the seed is short/partial and must NOT be " +
                "marked complete: " + string.Join("; ", failures));
    }

    private static readonly JsonSerializerOptions FingerprintJsonOptions = new(JsonSerializerDefaults.Web);

    // Stable hash of counts+seed (same Convert.ToHexString(SHA256.HashData(...))
    // convention as FarmLogo.ContentHash / IdempotencyMiddleware.Sha256) — a
    // re-run that recomputes the SAME counts from the SAME seed always
    // produces the SAME fingerprint (idempotent), and any real shortfall
    // changes it.
    //
    // #279 review Fix 3 (Agent A): states.DailyEntries (the Draft/Submitted/
    // Locked split) is deliberately EXCLUDED — it depends on where the
    // farm-local midnight lock-sweep boundary (DailyEntryLockSweep.
    // LockAfterDays) falls relative to this seeder's UTC "today" anchor at
    // the moment the sweep runs, so two otherwise-identical seed runs
    // straddling that boundary would fingerprint differently even though
    // every seeder-controlled COUNT (including DailyEntriesTotal) matches —
    // a latent flake, and not reproducible day-to-day. states.SalesOrders and
    // states.InventoryMovements stay in the hash: nothing time-based ever
    // moves a sales order or inventory movement between states after the
    // seeder writes it, so those splits are exactly as stable as counts. The
    // split itself is still reported — just via manifest.LifecycleStates
    // (#277), not folded into the fingerprint. Internal (not private) so
    // SimulationSeederTests can probe it directly rather than only through
    // the wall-clock-dependent integration path.
    internal static string ComputeFingerprint(int seed, SimulationManifestCounts counts, SimulationLifecycleStates states)
    {
        var canonical = JsonSerializer.Serialize(
            new { seed, counts, states.SalesOrders, states.InventoryMovements }, FingerprintJsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static readonly JsonSerializerOptions ManifestFileJsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private async Task WriteManifestFileAsync(string path, SimulationManifest manifest, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // Atomic: serialize the whole manifest (complete + fingerprint
        // included — they're computed last, in EmitManifestAsync, before this
        // is ever called) to a temp file in the SAME directory, then rename
        // over the final path. A reader (the #243 findings header, #277's
        // Playwright suite) never observes a partially-written manifest —
        // File.Move onto an existing path is a single filesystem rename, not
        // a truncate-then-write.
        var tempPath = path + ".tmp";
        await using (var stream = File.Create(tempPath))
            await JsonSerializer.SerializeAsync(stream, manifest, ManifestFileJsonOptions, ct);
        File.Move(tempPath, path, overwrite: true);

        logger.LogInformation("Simulation seed manifest written to {Path}.", path);
    }
}

public sealed record SimulationDailyEntryStates(int Draft, int Submitted, int Locked);

public sealed record SimulationSalesOrderStates(
    int Draft, int Confirmed, int Shipped, int Invoiced, int Cancelled, int Voided);

public sealed record SimulationInventoryMovementStates(int Purchase, int Usage, int Adjustment, int Discard);

public sealed record SimulationLifecycleStates(
    SimulationDailyEntryStates DailyEntries,
    SimulationSalesOrderStates SalesOrders,
    SimulationInventoryMovementStates InventoryMovements);

public sealed record SimulationManifestCounts(
    int Accounts,
    int Owners,
    int Managers,
    int Sales,
    int Workers,
    int ReadOnly,
    int UsersTotal,
    int Flocks,
    int DailyEntriesTotal,
    int EggLots,
    int SalesOrdersTotal,
    int Payments,
    int InventoryItems,
    int InventoryLots,
    int InventoryMovementsTotal,
    int FeedUsageRows,
    int WaterUsageRows,
    int ExpenseCategories,
    int Expenses);

// Not persisted directly — folded into ValidateCounts' failure messages
// above. Kept as its own type (rather than loose parameters) so
// ComputeExpectedCounts and ValidateCounts share one shape, and so a test can
// hand-build one for the fail-closed assertion.
public sealed record SimulationExpectedCounts(
    int Accounts,
    int Owners,
    int Managers,
    int Sales,
    int Workers,
    int ReadOnly,
    int UsersTotal,
    int Flocks,
    int DailyEntriesTotal,
    int DraftEntries,
    int SubmittedEntries,
    int LockedEntries,
    int EggLots,
    int SalesOrdersTotal,
    int SalesOrdersDraft,
    int SalesOrdersConfirmed,
    int Payments,
    int InventoryItems,
    int InventoryLots,
    int InventoryPurchaseMovements,
    int InventoryAdjustmentOrDiscardMovements,
    int InventoryUsageMovements,
    int InventoryMovementsTotal,
    int FeedUsageRows,
    int WaterUsageRows,
    int ExpenseCategories,
    int Expenses);

// #243 Task 3e — the seed's machine-readable completion artifact. schemaVersion
// lets a future breaking reshape be detected by consumers instead of silently
// misparsed; complete/fingerprint are always the last fields computed
// (EmitManifestAsync) and the manifest is only ever constructed AFTER
// ValidateCounts passes — there is no code path that produces one of these
// with complete: true for a short/partial seed.
public sealed record SimulationManifest(
    int SchemaVersion,
    int Seed,
    int HistoryDays,
    DateOnly GeneratedAtAnchor,
    SimulationManifestCounts Counts,
    SimulationLifecycleStates LifecycleStates,
    bool Complete,
    string Fingerprint);
