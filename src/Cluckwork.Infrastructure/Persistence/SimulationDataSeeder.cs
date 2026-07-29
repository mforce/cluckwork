namespace Cluckwork.Infrastructure.Persistence;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Accounts;
using Cluckwork.Application.Features.Accounts.UpdateFarmSettings;
using Cluckwork.Application.Features.DailyEntries;
using Cluckwork.Application.Features.DailyEntries.RecordDailyEntry;
using Cluckwork.Application.Features.DailyEntries.SubmitDailyEntry;
using Cluckwork.Application.Features.EggGrades;
using Cluckwork.Application.Features.Flocks.CreateFlock;
using Cluckwork.Application.Features.Users;
using Cluckwork.Application.Features.Users.AssignFlock;
using Cluckwork.Application.Features.Users.CreateUser;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Common;
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
// proof of the automatic lock sweep. Gated on Seed:Simulation (SeedOptions)
// AND Seed:Enabled; counts/timezone/password/email-domain/history length live
// in SimulationOptions.
//
// Unlike DemoDataSeeder, this seeder is FAIL-CLOSED: no try/catch, no
// partial-seed cleanup — any failure propagates straight out of SeedAsync and
// fails startup. A load-test environment with a silently short cast or a
// missing tenant fixture is worse than one that refuses to boot. Idempotency
// instead comes from per-entity existence checks (mirroring DatabaseSeeder /
// DemoDataSeeder), so a clean re-run converges instead of erroring on
// "already exists".
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
    DailyEntryLockSweep lockSweep,
    IClock clock,
    IOptions<SeedOptions> seedOptions,
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

    // The most recent couple of seeded days per flock stay Draft so both
    // lifecycle states exist in the seed; everything older is submitted.
    private const int DraftWindowDays = 2;

    // Deterministic id for the second, pristine tenant — mirrors
    // SeedDefaults.AccountId's fixed-GUID convention (…001) so both read as
    // related in logs/DB dumps. Never Guid.NewGuid(): a fixed id is what
    // makes "exactly two accounts after two starts" a checkable idempotency
    // guarantee instead of an ever-growing account table.
    public static readonly Guid SecondAccountId = new("0000000a-0000-0000-0000-000000000002");

    public async Task SeedAsync(CancellationToken ct = default)
    {
        var seed = seedOptions.Value;
        // Seed:Enabled=false disables ALL startup seeding, same as
        // DatabaseSeeder/DemoDataSeeder; Seed:Simulation is this seeder's own
        // opt-in on top of that.
        if (!seed.Simulation || !seed.Enabled) return;

        var sim = simulationOptions.Value;
        if (string.IsNullOrWhiteSpace(sim.CastPassword))
            throw new InvalidOperationException(
                "Seed:Simulation is enabled but Simulation:CastPassword is not set. " +
                "The sim cast has no fallback credential.");

        var accountId = SeedDefaults.AccountId;
        // Handlers and query filters need the tenant, which is unresolved at
        // startup — resolve it to the seeded account for this scope (matches
        // DemoDataSeeder).
        tenant.Resolve(accountId);

        // Reuse the seeded admin as Owner — never create a second Owner.
        var owner = await users.FindByEmailAsync(seed.AdminEmail);
        if (owner is null)
            throw new InvalidOperationException(
                $"Simulation seed requires the seeded admin ({seed.AdminEmail}) to already exist. " +
                "Set Seed:AdminEmail/Seed:AdminPassword so DatabaseSeeder creates the Owner first.");

        // Single injectable "today" anchor (#243 later task): captured ONCE,
        // threaded through everything below instead of each step calling
        // DateTime.UtcNow independently — deterministic, testable via a fixed
        // IClock, and #277's load-test assertions can reason about dates
        // relative to this one value.
        var today = clock.TodayUtc;

        var workerIds = await SeedCastAsync(accountId, sim, ct);
        var flockIds = await SeedFlockTopologyAsync(accountId, today, ct);
        await RestrictOneWorkerAsync(accountId, workerIds, flockIds, ct);
        // Timezone BEFORE any dated data exists (see SeedPrimaryTimeZoneAsync)
        // — production history below must see the account's real timezone.
        await SeedPrimaryTimeZoneAsync(sim, ct);
        await SeedProductionHistoryAsync(accountId, today, flockIds, sim, ct);
        await SeedSecondAccountAsync(ct);

        // Deterministic lock-sweep proof: run the sweep synchronously as part
        // of seeding rather than waiting on the DurableJobWorker's 30s poll,
        // so the day-9 sentinel entry seeded above is already Locked by the
        // time SeedAsync returns.
        await lockSweep.RunAsync(ct);

        logger.LogInformation("Simulation seed complete (Seed:Simulation=true).");
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

        var result = await createUser.HandleAsync(
            new CreateUserCommand(email, password, role, name), accountId, ct);
        Require(result, $"create cast user {email}");
        return result.Value;
    }

    // --- Minimal flock topology ----------------------------------------

    private async Task<IReadOnlyList<Guid>> SeedFlockTopologyAsync(
        Guid accountId, DateOnly today, CancellationToken ct)
    {
        (string Name, string Breed, DateOnly PlacementDate, int InitialCount)[] wanted =
        [
            ("Sim House A", "ISA Brown", today.AddDays(-30 * 7), 400),
            ("Sim House B", "Lohmann Brown", today.AddDays(-10 * 7), 350),
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
        var grades = (await eggGrades.ListActiveAsync(SeedDefaults.FarmId, ct))
            .Where(g => g.IsSaleable)
            .ToDictionary(g => g.Name, g => g.Id);
        if (grades.Count == 0)
            throw new InvalidOperationException("Simulation seed needs the default egg grades.");

        var historyDays = Math.Max(sim.HistoryDays, MinSentinelAgeDays);

        for (var i = 0; i < flockIds.Count; i++)
            await SeedFlockHistoryAsync(accountId, flockIds[i], baseline: 320 + i * 60, today, historyDays, grades, ct);
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

    // --- Second, pristine account (tenant-isolation fixture) -----------

    private async Task SeedSecondAccountAsync(CancellationToken ct)
    {
        // The account query filter would hide a second tenant's row —
        // IgnoreQueryFilters to see it regardless of which tenant is resolved
        // (matches DatabaseSeeder.SeedDefaultAccountAsync).
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
}
