namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Eggs;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #243 — the SimulationDataSeeder that builds the load-test cast (Managers /
// Sales / Workers / ReadOnly beyond the reused seeded admin), a minimal
// 2-flock topology with exactly one flock-restricted worker, the primary
// account's configured (non-UTC) timezone, and a second pristine account.
//
// Own factory (own Postgres container), not the shared IntegrationCollection:
// both this and BaselineSeedCurrencyTests write to the fixed
// SeedDefaults.AccountId, and other seeders running against the shared
// container would pollute the cast/flock/timezone counts asserted here.
public sealed class SimulationSeedFactory : CluckworkWebApplicationFactory
{
    public const string TimeZoneId = "America/Chicago";

    // Shallow on purpose (task 3a): the production-history seed loop is
    // O(HistoryDays * flocks) real handler round-trips against Testcontainers
    // Postgres — 90 (the real SimulationOptions default) would make every
    // test in this fixture slow. 12 still clears MinSentinelAgeDays (9) with
    // margin, so the Draft/Submitted/Locked bands asserted below hold
    // regardless of how the account timezone happens to skew against UTC at
    // the moment the suite runs.
    public const int HistoryDays = 12;

    // Runtime-generated — never a hardcoded credential (repo policy).
    public string AdminEmail { get; } = $"sim-admin-{Guid.NewGuid():N}@test.local";
    public string AdminPassword { get; } = $"Aa1!{Guid.NewGuid():N}";
    public string CastPassword { get; } = $"Aa1!{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Seed:Enabled", "true");
        // Explicit, not just the SeedOptions default: a stray Seed__Demo=true
        // in the environment would seed demo flocks and pollute the flock
        // count asserted below — tests must be hermetic.
        builder.UseSetting("Seed:Demo", "false");
        builder.UseSetting("Seed:Simulation", "true");
        builder.UseSetting("Seed:AdminEmail", AdminEmail);
        builder.UseSetting("Seed:AdminPassword", AdminPassword);
        builder.UseSetting("Simulation:CastPassword", CastPassword);
        builder.UseSetting("Simulation:TimeZoneId", TimeZoneId);
        builder.UseSetting("Simulation:HistoryDays", HistoryDays.ToString());
    }
}

public sealed class SimulationSeederTests(SimulationSeedFactory factory)
    : IClassFixture<SimulationSeedFactory>
{
    // SimulationOptions defaults, left un-overridden by the factory above:
    // Managers=1, Sales=1, Workers=3, ReadOnly=4, EmailDomain="sim.local".
    private const string EmailDomain = "sim.local";
    private const int ExpectedManagers = 1;
    private const int ExpectedSales = 1;
    private const int ExpectedWorkers = 3;
    private const int ExpectedReadOnly = 4;
    private const int ExpectedCastUsers = ExpectedManagers + ExpectedSales + ExpectedWorkers + ExpectedReadOnly;

    [Fact]
    public async Task SimulationSeed_BuildsCastWithoutDuplicatingTheOwner()
    {
        using var client = factory.CreateClient(); // forces host init / first startup seed.
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Total headcount on the primary account: the reused Owner (admin) +
        // exactly the configured cast — no 11th/duplicate Owner.
        var accountUsers = await db.Users
            .Where(u => u.AccountId == SeedDefaults.AccountId)
            .ToListAsync();
        Assert.Equal(1 + ExpectedCastUsers, accountUsers.Count);

        var owners = (await users.GetUsersInRoleAsync(Roles.Owner))
            .Where(u => u.AccountId == SeedDefaults.AccountId)
            .ToList();
        var owner = Assert.Single(owners);
        Assert.Equal(factory.AdminEmail, owner.Email);

        var manager = await users.FindByEmailAsync($"sim-manager-1@{EmailDomain}");
        Assert.NotNull(manager);
        Assert.Contains(Roles.Manager, await users.GetRolesAsync(manager!));

        var sales = await users.FindByEmailAsync($"sim-sales-1@{EmailDomain}");
        Assert.NotNull(sales);
        Assert.Contains(Roles.Sales, await users.GetRolesAsync(sales!));

        var readOnly = await users.FindByEmailAsync($"sim-readonly-1@{EmailDomain}");
        Assert.NotNull(readOnly);
        Assert.Contains(Roles.ReadOnly, await users.GetRolesAsync(readOnly!));

        // Workers deliberately carry NO role row (Roles.cs) — the absence IS
        // the worker.
        var worker1 = await users.FindByEmailAsync($"sim-worker-1@{EmailDomain}");
        Assert.NotNull(worker1);
        Assert.Empty(await users.GetRolesAsync(worker1!));
    }

    [Fact]
    public async Task SimulationSeed_RestrictsExactlyOneWorkerToOneOfTwoFlocks()
    {
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var flocks = await db.Flocks.IgnoreQueryFilters()
            .Where(f => f.AccountId == SeedDefaults.AccountId)
            .ToListAsync();
        Assert.Equal(2, flocks.Count);

        var scopedAssignments = await db.UserRoleAssignments.IgnoreQueryFilters()
            .Where(a => a.AccountId == SeedDefaults.AccountId && a.FlockId != null)
            .ToListAsync();
        var assignment = Assert.Single(scopedAssignments);

        // Genuinely narrowed: assigned to one flock, the other left out.
        Assert.Contains(assignment.FlockId!.Value, flocks.Select(f => f.Id));
        Assert.Single(flocks, f => f.Id != assignment.FlockId!.Value);
    }

    [Fact]
    public async Task SimulationSeed_SetsThePrimaryAccountTimeZone()
    {
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var account = await db.Accounts.IgnoreQueryFilters()
            .SingleAsync(a => a.Id == SeedDefaults.AccountId);
        Assert.Equal(SimulationSeedFactory.TimeZoneId, account.TimeZoneId);
    }

    // DatabaseSeeder hardcodes the primary account to UTC on first creation —
    // this proves the simulation seeder actually overrides it afterwards,
    // rather than the assertion above passing by coincidence.
    [Fact]
    public void SimulationSeed_ConfiguredTimeZone_IsNotUtc() =>
        Assert.NotEqual("UTC", SimulationSeedFactory.TimeZoneId);

    [Fact]
    public async Task SimulationSeed_IsIdempotent_ExactlyTwoAccountsAfterTwoHostStarts()
    {
        using var firstClient = factory.CreateClient(); // first startup (may already be built).
        using (var secondHost = factory.WithWebHostBuilder(_ => { }))
        using (var secondClient = secondHost.CreateClient()) // second full Program.cs run, same DB.
        {
            // Nothing to do with the client — creating it is what forces the
            // second host (and its startup seed) to build.
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var accountCount = await db.Accounts.IgnoreQueryFilters().CountAsync();

        // The primary (SeedDefaults.AccountId) + the deterministic second sim
        // account — a re-run must not mint a third.
        Assert.Equal(2, accountCount);
    }

    // #243 later task: production daily-entry history on the two Task-2
    // flocks, one entry per flock per day, plus the deterministic lock-sweep
    // proof (the seeder runs DailyEntryLockSweep itself before SeedAsync
    // returns — no wait on the DurableJobWorker's 30s poll).
    [Fact]
    public async Task SimulationSeed_SeedsProductionHistoryWithMixedLifecycleStatesAndAnAlreadyLockedSentinel()
    {
        using var client = factory.CreateClient(); // forces host init / first startup seed.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var flocks = await db.Flocks.IgnoreQueryFilters()
            .Where(f => f.AccountId == SeedDefaults.AccountId)
            .ToListAsync();
        Assert.Equal(2, flocks.Count);

        var entries = await db.DailyEntries.IgnoreQueryFilters()
            .Where(e => e.AccountId == SeedDefaults.AccountId)
            .ToListAsync();

        // Exactly one entry per (flock, day) across the whole shallow history
        // window — nothing skipped, nothing doubled on a single seed pass.
        Assert.Equal(flocks.Count * SimulationSeedFactory.HistoryDays, entries.Count);
        Assert.All(flocks, f => Assert.Contains(entries, e => e.FlockId == f.Id));

        // The seeder's own within-SeedAsync sweep call must already have run:
        // at least one entry is Locked without the test driving the sweep
        // itself or waiting on the background job worker.
        Assert.Contains(entries, e => e.Status == DailyEntryStatus.Locked);
        // A recent-but-old-enough entry stays Submitted (not yet lockable).
        Assert.Contains(entries, e => e.Status == DailyEntryStatus.Submitted);
        // The most recent seeded days stay Draft so that lifecycle state is
        // populated too.
        Assert.Contains(entries, e => e.Status == DailyEntryStatus.Draft);

        // No other status should ever appear — the seeder never adjusts or
        // voids what it seeds.
        Assert.All(entries, e => Assert.True(
            e.Status is DailyEntryStatus.Draft or DailyEntryStatus.Submitted or DailyEntryStatus.Locked,
            $"Unexpected daily entry status {e.Status} on {e.Date}."));
    }

    [Fact]
    public async Task SimulationSeed_ProductionHistory_IsIdempotent_NoDuplicateEntriesAfterTwoHostStarts()
    {
        using var firstClient = factory.CreateClient(); // first startup (may already be built).
        using (var secondHost = factory.WithWebHostBuilder(_ => { }))
        using (var secondClient = secondHost.CreateClient()) // second full Program.cs run, same DB.
        {
            // Nothing to do with the client — creating it is what forces the
            // second host (and its startup seed, and its sweep call) to build.
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var flocks = await db.Flocks.IgnoreQueryFilters()
            .Where(f => f.AccountId == SeedDefaults.AccountId)
            .ToListAsync();
        var entries = await db.DailyEntries.IgnoreQueryFilters()
            .Where(e => e.AccountId == SeedDefaults.AccountId)
            .ToListAsync();

        // A second full seed pass converges rather than doubling: still
        // exactly one entry per (flock, day), and each natural key
        // (flock, date) is unique — the RecordDailyEntry natural-key
        // existence check in SeedFlockHistoryAsync skipped every day the
        // first pass already created.
        Assert.Equal(flocks.Count * SimulationSeedFactory.HistoryDays, entries.Count);
        var duplicateKeys = entries
            .GroupBy(e => (e.FlockId, e.Date))
            .Where(g => g.Count() > 1)
            .ToList();
        Assert.Empty(duplicateKeys);
    }
}
