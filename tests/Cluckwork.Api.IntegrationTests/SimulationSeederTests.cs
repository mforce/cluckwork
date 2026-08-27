namespace Cluckwork.Api.IntegrationTests;

using System.IO;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Auditing;
using Cluckwork.Domain.Eggs;
using Cluckwork.Domain.Inventory;
using Cluckwork.Domain.Sales;
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
public sealed class SimulationSeedFactory : CluckworkWebApplicationFactory, IAsyncLifetime
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
    public string AdminPassword { get; } = TestHarness.Password;
    public string CastPassword { get; } = $"Aa1!{Guid.NewGuid():N}";

    // #279 — SimulationDataSeeder.SeedAsync() no longer returns the manifest
    // directly (it returns a shared SeedResult, mirroring DemoDataSeeder);
    // the manifest is a separate artifact, written only when
    // Simulation:CredentialOutputPath is configured — same as the real `seed
    // --profile simulation` command. Pointing it at a temp file gives the
    // manifest-content tests below something to read back.
    public string ManifestPath { get; } =
        Path.Combine(Path.GetTempPath(), $"sim-manifest-{Guid.NewGuid():N}.json");

    // The SeedResult of the one seed run InitializeAsync performs below.
    public SeedResult SeedResult { get; private set; } = null!;

    // #500 — the ids of every audit row that seed run wrote, snapshotted before
    // any [Fact] gets a chance to add one of its own.
    //
    // "Every audit row in this account" is NOT the seeder's output: facts in
    // this class drive real endpoints, and the account export writes an
    // Account.Export row. Whether such a row exists when a given fact runs
    // depends on xUnit's fact ordering, which nothing here pins — so an
    // assertion over the unfiltered table is order-dependent by construction.
    // This snapshot is what makes "walk every action the SEEDER produced"
    // answerable at all.
    public IReadOnlyCollection<Guid> SeededAuditEventIds { get; private set; } = [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Simulation:CastPassword", CastPassword);
        builder.UseSetting("Simulation:TimeZoneId", TimeZoneId);
        builder.UseSetting("Simulation:HistoryDays", HistoryDays.ToString());
        builder.UseSetting("Simulation:CredentialOutputPath", ManifestPath);
    }

    // #279 — SimulationDataSeeder no longer boot-seeds (it's wired only into
    // the `seed --profile simulation` CLI command, never Program.cs's startup
    // block), so this factory has to do what that command does. #283 — the
    // default account/Admin role/saleable egg grades are now migration-baked
    // static reference data (no Seed:* config, no runtime seeder); the Owner
    // admin itself is NOT, so it's seeded directly here — standing in for a
    // real `bootstrap-admin` run — BEFORE calling SimulationDataSeeder
    // directly (the same resolve-and-seed pattern DemoSeedTests uses for
    // DemoDataSeeder). This runs ONCE before any [Fact] in the class (xUnit's
    // IClassFixture contract), so the fixture is in place for every Fact
    // below; the "two full host restarts" idempotency Facts still call
    // SeedAsync() a second time explicitly against their own second host.
    //
    // NOTE: redeclaring `IAsyncLifetime` above (already implemented by the
    // base class) is required for xUnit to actually dispatch to THIS
    // override — CluckworkWebApplicationFactory.InitializeAsync() is not
    // virtual, so a `new` method alone would be silently skipped by any code
    // that calls it through an IAsyncLifetime reference (as xUnit does).
    public new async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await this.SeedUserAsync(SeedDefaults.AccountId, AdminEmail, Roles.Owner);

        using var scope = Services.CreateScope();
        SeedResult = await scope.ServiceProvider.GetRequiredService<SimulationDataSeeder>().SeedAsync();
        if (!SeedResult.IsSuccess)
            throw new InvalidOperationException(
                $"Simulation seed setup failed ({SeedResult.Status}): {SeedResult.Message}");

        SeededAuditEventIds = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .AuditEvents.IgnoreQueryFilters()
            .Where(e => e.AccountId == SeedDefaults.AccountId)
            .Select(e => e.Id)
            .ToHashSetAsync();
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        // Best-effort: a leftover temp manifest file is harmless but not
        // worth keeping around.
        try
        {
            if (File.Exists(ManifestPath)) File.Delete(ManifestPath);
        }
        catch
        {
            // Cleanup only — never fail the suite over a stray temp file.
        }
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
        using var client = factory.CreateClient(); // host + simulation data already seeded via InitializeAsync().
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

    // #279 review Fix 6: the fixture's one InitializeAsync seed run — against a
    // fresh database — is the genuine FIRST run and must report Seeded (the
    // reruns above/below assert the AlreadySeeded side of the same signal).
    [Fact]
    public void SimulationSeed_FirstRun_ReportsSeeded() =>
        Assert.Equal(SeedStatus.Seeded, factory.SeedResult.Status);

    [Fact]
    public async Task SimulationSeed_IsIdempotent_ExactlyTwoAccountsAfterTwoHostStarts()
    {
        using var firstClient = factory.CreateClient(); // first host: simulation data already seeded via InitializeAsync().
        using var secondHost = factory.WithWebHostBuilder(_ => { });
        using var secondClient = secondHost.CreateClient(); // second full Program.cs run, same DB (base re-seeds).
        using (var secondScope = secondHost.Services.CreateScope())
        {
            // #279 — boot alone no longer seeds simulation data; call it
            // explicitly to prove SeedAsync itself converges across a
            // genuine second full Program.cs run against the same DB.
            var result = await secondScope.ServiceProvider.GetRequiredService<SimulationDataSeeder>().SeedAsync();
            Assert.True(result.IsSuccess, result.Message);
            // #279 review Fix 6: a rerun over the already-seeded fixture must
            // report AlreadySeeded (the durable completion marker), not Seeded.
            Assert.Equal(SeedStatus.AlreadySeeded, result.Status);
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
        using var client = factory.CreateClient(); // host + simulation data already seeded via InitializeAsync().
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

    // #279 review Fix 1 (codex): SeedProductionHistoryAsync writes the same
    // day-offset range for EVERY flock regardless of when that flock was
    // placed — a flock placed more recently than the oldest entry date would
    // end up with history predating its own existence. Both flocks are now
    // placed strictly older than the whole history window (see
    // SimulationDataSeeder.FlockPlacementMarginDays), so this must hold for
    // every seeded flock/entry pair.
    [Fact]
    public async Task SimulationSeed_EveryDailyEntryDate_IsOnOrAfterItsFlockPlacementDate()
    {
        using var client = factory.CreateClient(); // host + simulation data already seeded via InitializeAsync().
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var flocks = await db.Flocks.IgnoreQueryFilters()
            .Where(f => f.AccountId == SeedDefaults.AccountId)
            .ToListAsync();
        var entries = await db.DailyEntries.IgnoreQueryFilters()
            .Where(e => e.AccountId == SeedDefaults.AccountId)
            .ToListAsync();

        var placementByFlock = flocks.ToDictionary(f => f.Id, f => f.PlacementDate);
        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.True(
            e.Date >= placementByFlock[e.FlockId],
            $"Daily entry dated {e.Date:yyyy-MM-dd} for flock {e.FlockId} predates that flock's " +
            $"placement date {placementByFlock[e.FlockId]:yyyy-MM-dd}."));
    }

    [Fact]
    public async Task SimulationSeed_ProductionHistory_IsIdempotent_NoDuplicateEntriesAfterTwoHostStarts()
    {
        using var firstClient = factory.CreateClient(); // first host: simulation data already seeded via InitializeAsync().
        using var secondHost = factory.WithWebHostBuilder(_ => { });
        using var secondClient = secondHost.CreateClient(); // second full Program.cs run, same DB (base re-seeds).
        using (var secondScope = secondHost.Services.CreateScope())
        {
            // #279 — boot alone no longer seeds simulation data (or re-runs
            // the sweep); call SeedAsync explicitly to prove the second pass
            // still converges.
            var result = await secondScope.ServiceProvider.GetRequiredService<SimulationDataSeeder>().SeedAsync();
            Assert.True(result.IsSuccess, result.Message);
            // #279 review Fix 6: a rerun over the already-seeded fixture must
            // report AlreadySeeded (the durable completion marker), not Seeded.
            Assert.Equal(SeedStatus.AlreadySeeded, result.Status);
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

    // #243 Task 3b: sales catalog + customers + orders across the lifecycle +
    // FIFO lot depletion + payments, seeded on top of the Task-3a production
    // history above.
    [Fact]
    public async Task SimulationSeed_SeedsProductCatalogAndCustomers()
    {
        using var client = factory.CreateClient(); // host + simulation data already seeded via InitializeAsync().
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var products = await db.Products.IgnoreQueryFilters()
            .Where(p => p.AccountId == SeedDefaults.AccountId)
            .ToListAsync();
        // #396: Cracked/Dirty are saleable grades that submit now mints lots
        // for, so the catalog covers them too — without a product those lots
        // are stock nothing can sell.
        foreach (var name in new[]
                 {
                     "Sim Large Eggs", "Sim Medium Eggs", "Sim Small Eggs",
                     "Sim Cracked Eggs", "Sim Dirty Eggs",
                 })
            Assert.Contains(products, p => p.Name == name);

        var customers = await db.Customers.IgnoreQueryFilters()
            .Where(c => c.AccountId == SeedDefaults.AccountId)
            .ToListAsync();
        foreach (var name in new[] { "Sim Customer 1", "Sim Customer 2", "Sim Customer 3" })
            Assert.Contains(customers, c => c.Name == name);
    }

    [Fact]
    public async Task SimulationSeed_SeedsSalesOrdersAcrossTheLifecycleWithAPartialPayment()
    {
        using var client = factory.CreateClient(); // host + simulation data already seeded via InitializeAsync().
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var orders = await db.SalesOrders.IgnoreQueryFilters()
            .Where(o => o.AccountId == SeedDefaults.AccountId)
            .ToListAsync();
        var payments = await db.Payments.IgnoreQueryFilters()
            .Where(p => p.AccountId == SeedDefaults.AccountId)
            .ToListAsync();

        // Draft: created + items added, never confirmed.
        Assert.Contains(orders, o => o.Status == SalesOrderStatus.Draft);

        // Confirmed, unpaid — a distinct order from the partially-paid one
        // below, and also the "voidable confirmed order" a later hazard pass
        // can act on (this seeder never voids anything itself).
        Assert.Contains(orders, o =>
            o.Status == SalesOrderStatus.Confirmed && payments.All(p => p.SalesOrderId != o.Id));

        // Confirmed + partially paid: a real payment strictly less than the
        // order total (never accidentally fully settled).
        var partiallyPaid = orders.SingleOrDefault(o =>
            o.Status == SalesOrderStatus.Confirmed && payments.Any(p => p.SalesOrderId == o.Id));
        Assert.NotNull(partiallyPaid);
        var payment = Assert.Single(payments, p => p.SalesOrderId == partiallyPaid!.Id);
        Assert.False(payment.Voided);
        Assert.True(payment.AmountMinorUnits < partiallyPaid.TotalAmount.MinorUnits,
            $"Payment {payment.AmountMinorUnits} should be less than the order total {partiallyPaid.TotalAmount.MinorUnits}.");
        Assert.True(payment.AmountMinorUnits > 0);

        // The seeder never voids what it seeds.
        Assert.DoesNotContain(orders, o => o.Status == SalesOrderStatus.Voided);
    }

    [Fact]
    public async Task SimulationSeed_ConfirmedOrders_DepleteEggLotsFifoFromASharedOldLotPool()
    {
        using var client = factory.CreateClient(); // host + simulation data already seeded via InitializeAsync().
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var lots = await db.EggLots.IgnoreQueryFilters()
            .Where(l => l.AccountId == SeedDefaults.AccountId)
            .ToListAsync();
        // Some lots drawn down by the confirmed orders' FIFO allocation.
        Assert.Contains(lots, l => l.QuantityAvailable < l.QuantityProduced);

        var allocations = await db.SalesOrderAllocations.IgnoreQueryFilters()
            .Where(a => a.AccountId == SeedDefaults.AccountId)
            .ToListAsync();
        Assert.NotEmpty(allocations);

        // All three confirmed orders sell the same product/grade in small
        // quantities relative to a single day's lot (SimulationDataSeeder's
        // ConfirmedOrderQuantityEggs) — FIFO (oldest ProductionDate first)
        // means they compete for the SAME shallow pool of old lots rather
        // than each landing on disjoint fresh stock: at least one lot has
        // allocation rows from more than one distinct sales order.
        var sharedLot = allocations
            .GroupBy(a => a.EggLotId)
            .FirstOrDefault(g => g.Select(a => a.SalesOrderId).Distinct().Count() > 1);
        Assert.NotNull(sharedLot);
    }

    [Fact]
    public async Task SimulationSeed_Sales_IsIdempotent_NoDuplicatesAfterTwoHostStarts()
    {
        using var firstClient = factory.CreateClient(); // first host: simulation data already seeded via InitializeAsync().
        using var secondHost = factory.WithWebHostBuilder(_ => { });
        using var secondClient = secondHost.CreateClient(); // second full Program.cs run, same DB (base re-seeds).
        using (var secondScope = secondHost.Services.CreateScope())
        {
            // #279 — boot alone no longer seeds simulation data; call it
            // explicitly to prove the second pass still converges.
            var result = await secondScope.ServiceProvider.GetRequiredService<SimulationDataSeeder>().SeedAsync();
            Assert.True(result.IsSuccess, result.Message);
            // #279 review Fix 6: a rerun over the already-seeded fixture must
            // report AlreadySeeded (the durable completion marker), not Seeded.
            Assert.Equal(SeedStatus.AlreadySeeded, result.Status);
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var products = await db.Products.IgnoreQueryFilters()
            .Where(p => p.AccountId == SeedDefaults.AccountId)
            .ToListAsync();
        var customers = await db.Customers.IgnoreQueryFilters()
            .Where(c => c.AccountId == SeedDefaults.AccountId)
            .ToListAsync();
        var orders = await db.SalesOrders.IgnoreQueryFilters()
            .Where(o => o.AccountId == SeedDefaults.AccountId)
            .ToListAsync();

        // A second full seed pass converges rather than doubling — same
        // five products (#396 added the two condition grades), three
        // customers, six orders (2 draft + 2
        // confirmed-unpaid + 1 confirmed-partially-paid + 1 recurring
        // confirmed, #243 Task 3d's RecurringStartDay/RecurringCadenceDays
        // drip — exactly one point lands inside a 12-day HistoryDays window)
        // as a single pass.
        Assert.Equal(5, products.Count);
        Assert.Equal(3, customers.Count);
        Assert.Equal(6, orders.Count);
        Assert.DoesNotContain(products.GroupBy(p => p.Name), g => g.Count() > 1);
        Assert.DoesNotContain(customers.GroupBy(c => c.Name), g => g.Count() > 1);
        Assert.DoesNotContain(orders.GroupBy(o => (o.CustomerId, o.OrderDate)), g => g.Count() > 1);
    }

    // #243 Task 3c: inventory items + an opening purchase (lot) + at least
    // one adjustment/discard per item, feed/water usage across past days,
    // and expense categories + expenses — seeded on top of the flock
    // topology and production history above.

    // SimulationDataSeeder's own private constants — duplicated here rather
    // than exposed, same convention ExpectedManagers/ExpectedWorkers above
    // already use for the cast counts.
    private static readonly string[] ExpectedInventoryItemNames = ["Sim Layer Feed", "Sim Pine Shavings"];
    private const int ExpectedFeedUsageDays = 4;
    private const int ExpectedWaterUsageDays = 4;
    private static readonly string[] ExpectedExpenseCategoryNames = ["Sim Utilities", "Sim Repairs & Maintenance"];
    private static readonly string[] ExpectedExpenseDescriptions =
    [
        "Sim Electricity Bill", "Sim Water Utility Bill", "Sim Coop Roof Repair", "Sim Feeder Replacement Part",
    ];

    [Fact]
    public async Task SimulationSeed_SeedsInventoryItemsWithAnOpeningPurchaseAndAnAdjustment()
    {
        using var client = factory.CreateClient(); // host + simulation data already seeded via InitializeAsync().
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var items = await db.InventoryItems.IgnoreQueryFilters()
            .Where(i => i.AccountId == SeedDefaults.AccountId)
            .ToListAsync();
        foreach (var name in ExpectedInventoryItemNames)
            Assert.Contains(items, i => i.Name == name);

        foreach (var item in items.Where(i => ExpectedInventoryItemNames.Contains(i.Name)))
        {
            // Exactly one opening lot per item.
            var lot = Assert.Single(await db.InventoryLots.IgnoreQueryFilters()
                .Where(l => l.InventoryItemId == item.Id).ToListAsync());
            Assert.True(lot.QuantityReceived > 0);
            // The adjustment/discard below drew the lot down below what was
            // received, without exhausting it.
            Assert.True(lot.QuantityAvailable < lot.QuantityReceived);
            Assert.True(lot.QuantityAvailable > 0);

            var movements = await db.InventoryMovements.IgnoreQueryFilters()
                .Where(m => m.InventoryLotId == lot.Id).ToListAsync();
            Assert.Contains(movements, m => m.Type == InventoryMovementType.Purchase);
            Assert.Contains(movements,
                m => m.Type is InventoryMovementType.Adjustment or InventoryMovementType.Discard);
        }
    }

    [Fact]
    public async Task SimulationSeed_SeedsFeedAndWaterUsageAcrossPastDays()
    {
        using var client = factory.CreateClient(); // host + simulation data already seeded via InitializeAsync().
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var flocks = await db.Flocks.IgnoreQueryFilters()
            .Where(f => f.AccountId == SeedDefaults.AccountId)
            .ToListAsync();
        Assert.Equal(2, flocks.Count);

        var feedUsages = await db.FeedUsages.IgnoreQueryFilters()
            .Where(u => u.AccountId == SeedDefaults.AccountId)
            .ToListAsync();
        Assert.Equal(flocks.Count * ExpectedFeedUsageDays, feedUsages.Count);
        Assert.All(flocks, f => Assert.Contains(feedUsages, u => u.FlockId == f.Id));
        Assert.All(feedUsages, u => Assert.True(u.Quantity > 0));
        // Lot-cost costing (spec §12.4) — every seeded usage row priced.
        Assert.All(feedUsages, u => Assert.True(u.EstimatedCost.MinorUnits > 0));

        var waterUsages = await db.WaterUsages.IgnoreQueryFilters()
            .Where(u => u.AccountId == SeedDefaults.AccountId)
            .ToListAsync();
        Assert.Equal(flocks.Count * ExpectedWaterUsageDays, waterUsages.Count);
        Assert.All(flocks, f => Assert.Contains(waterUsages, u => u.FlockId == f.Id));
        Assert.All(waterUsages, u => Assert.True(u.Quantity > 0));
    }

    [Fact]
    public async Task SimulationSeed_SeedsExpenseCategoriesAndExpenses()
    {
        using var client = factory.CreateClient(); // host + simulation data already seeded via InitializeAsync().
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var categories = await db.ExpenseCategories.IgnoreQueryFilters()
            .Where(c => c.AccountId == SeedDefaults.AccountId)
            .ToListAsync();
        foreach (var name in ExpectedExpenseCategoryNames)
            Assert.Contains(categories, c => c.Name == name);

        var expenses = await db.Expenses.IgnoreQueryFilters()
            .Where(e => e.AccountId == SeedDefaults.AccountId)
            .ToListAsync();
        foreach (var description in ExpectedExpenseDescriptions)
            Assert.Contains(expenses, e => e.Description == description);
        Assert.All(expenses, e => Assert.True(e.AmountMinorUnits > 0));
        // At least one expense is allocated to a flock (admin-tier data
        // covers both farm-wide and flock-attributed expenses).
        Assert.Contains(expenses, e => e.FlockId is not null);
    }

    [Fact]
    public async Task SimulationSeed_InventoryFeedWaterExpenses_IsIdempotent_NoDuplicatesAfterTwoHostStarts()
    {
        using var firstClient = factory.CreateClient(); // first host: simulation data already seeded via InitializeAsync().
        using var secondHost = factory.WithWebHostBuilder(_ => { });
        using var secondClient = secondHost.CreateClient(); // second full Program.cs run, same DB (base re-seeds).
        using (var secondScope = secondHost.Services.CreateScope())
        {
            // #279 — boot alone no longer seeds simulation data; call it
            // explicitly to prove the second pass still converges.
            var result = await secondScope.ServiceProvider.GetRequiredService<SimulationDataSeeder>().SeedAsync();
            Assert.True(result.IsSuccess, result.Message);
            // #279 review Fix 6: a rerun over the already-seeded fixture must
            // report AlreadySeeded (the durable completion marker), not Seeded.
            Assert.Equal(SeedStatus.AlreadySeeded, result.Status);
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var flocks = await db.Flocks.IgnoreQueryFilters()
            .Where(f => f.AccountId == SeedDefaults.AccountId)
            .ToListAsync();

        var items = await db.InventoryItems.IgnoreQueryFilters()
            .Where(i => i.AccountId == SeedDefaults.AccountId)
            .ToListAsync();
        Assert.Equal(2, items.Count);
        Assert.DoesNotContain(items.GroupBy(i => i.Name), g => g.Count() > 1);

        var lots = await db.InventoryLots.IgnoreQueryFilters()
            .Where(l => l.AccountId == SeedDefaults.AccountId)
            .ToListAsync();
        // Exactly one opening lot per item — a second pass must not mint a
        // second purchase.
        Assert.Equal(2, lots.Count);

        var adjustmentMovements = await db.InventoryMovements.IgnoreQueryFilters()
            .Where(m => m.AccountId == SeedDefaults.AccountId
                        && (m.Type == InventoryMovementType.Adjustment || m.Type == InventoryMovementType.Discard))
            .ToListAsync();
        Assert.Equal(2, adjustmentMovements.Count);

        var feedUsages = await db.FeedUsages.IgnoreQueryFilters()
            .Where(u => u.AccountId == SeedDefaults.AccountId)
            .ToListAsync();
        Assert.Equal(flocks.Count * ExpectedFeedUsageDays, feedUsages.Count);
        Assert.DoesNotContain(feedUsages.GroupBy(u => (u.FlockId, u.InventoryItemId, u.Date)), g => g.Count() > 1);

        var waterUsages = await db.WaterUsages.IgnoreQueryFilters()
            .Where(u => u.AccountId == SeedDefaults.AccountId)
            .ToListAsync();
        Assert.Equal(flocks.Count * ExpectedWaterUsageDays, waterUsages.Count);
        Assert.DoesNotContain(waterUsages.GroupBy(u => (u.FlockId, u.Date)), g => g.Count() > 1);

        var categories = await db.ExpenseCategories.IgnoreQueryFilters()
            .Where(c => c.AccountId == SeedDefaults.AccountId)
            .ToListAsync();
        Assert.Equal(2, categories.Count);
        Assert.DoesNotContain(categories.GroupBy(c => c.Name), g => g.Count() > 1);

        var expenses = await db.Expenses.IgnoreQueryFilters()
            .Where(e => e.AccountId == SeedDefaults.AccountId)
            .ToListAsync();
        // 4 hardcoded + 1 recurring (#243 Task 3d's drip — one point lands
        // inside a 12-day HistoryDays window, same as the sales recurring
        // series above).
        Assert.Equal(5, expenses.Count);
        Assert.DoesNotContain(expenses.GroupBy(e => e.Description), g => g.Count() > 1);
    }

    // #243 Task 3d — smoke test locking in the depth/density decision
    // recorded in SimulationDataSeeder's header comment: the report and
    // export endpoints (the load test's heaviest read paths) must return
    // non-trivial volume against the sim seed, not a near-empty result. Real,
    // authenticated HTTP calls as the seeded Owner — same pattern
    // ReportsTests/ExportTests use, against this fixture's shallow (but
    // representative) 12-day HistoryDays.

    private static DateOnly UtcToday => DateOnly.FromDateTime(DateTime.UtcNow);

    private sealed record ProductionDayDto(DateOnly Date, int TotalEggs);
    private sealed record ProductionReportDto(
        List<ProductionDayDto> Days, int TotalEggs, int TotalSellable, int TotalDeaths);
    private sealed record SalesSummaryDto(
        int ConfirmedCount, long RevenueMinorUnits, long PaidMinorUnits,
        long OutstandingMinorUnits, int VoidedCount);
    private sealed record ExpenseCategoryTotalDto(Guid ExpenseCategoryId, string Name, long TotalMinorUnits);
    private sealed record ExpenseSummaryDto(List<ExpenseCategoryTotalDto> Categories, long GrandTotalMinorUnits);
    private sealed record ProfitReportDto(long RevenueMinorUnits, long ExpensesMinorUnits, long ProfitMinorUnits);

    [Fact]
    public async Task SimulationSeed_ReportEndpoints_ReturnNonTrivialVolumeAcrossTheHistoryWindow()
    {
        using var seedClient = factory.CreateClient(); // host + simulation data already seeded via InitializeAsync().
        // Not LoginForAccessTokenAsync: this uses the factory's OWN email
        // (factory.AdminEmail), not a fresh one — TestHarness.Password is the
        // right password (SeedUserAsync always uses it), but the email must
        // match the specific Owner InitializeAsync seeded and the seeder reused.
        var loginResponse = await factory.TryLoginAsync(factory.AdminEmail, factory.AdminPassword);
        loginResponse.EnsureSuccessStatusCode();
        var tokens = await TestHarness.ReadTokensAsync(loginResponse);
        var client = factory.CreateAuthedClient(tokens.AccessToken);

        // Safely in the past on both ends regardless of UTC-vs-farm-local
        // skew (same reasoning SimulationDataSeeder's own header comment
        // uses for its seeded dates) — covers the whole seeded window (days
        // 1..HistoryDays) with margin on both sides.
        var from = UtcToday.AddDays(-(SimulationSeedFactory.HistoryDays + 1));
        var to = UtcToday.AddDays(-1);
        var range = $"from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}";

        var production = await client.GetFromJsonAsync<ProductionReportDto>(
            $"/api/v1/reports/production?{range}");
        // One row per calendar day in [from, to] — multiple periods, not a
        // single day or an empty report.
        Assert.Equal(SimulationSeedFactory.HistoryDays + 1, production!.Days.Count);
        Assert.True(production.TotalEggs > 0);
        Assert.True(production.TotalSellable > 0);

        var sales = await client.GetFromJsonAsync<SalesSummaryDto>($"/api/v1/reports/sales?{range}");
        // 3 lifecycle confirmed orders (days 2/5/6) + 1 recurring (#243 Task
        // 3d's drip, day 9) — spread across the window, not a single-order
        // result.
        Assert.Equal(4, sales!.ConfirmedCount);
        Assert.True(sales.RevenueMinorUnits > 0);
        Assert.True(sales.PaidMinorUnits > 0);
        Assert.True(sales.OutstandingMinorUnits > 0);
        Assert.Equal(0, sales.VoidedCount);

        var expenses = await client.GetFromJsonAsync<ExpenseSummaryDto>($"/api/v1/reports/expenses?{range}");
        // Both categories carry spend — not a single-category, single-row
        // summary.
        Assert.Equal(2, expenses!.Categories.Count);
        Assert.All(expenses.Categories, c => Assert.True(c.TotalMinorUnits > 0));
        Assert.True(expenses.GrandTotalMinorUnits > 0);

        var profit = await client.GetFromJsonAsync<ProfitReportDto>($"/api/v1/reports/profit?{range}");
        // Cross-checks against the two summaries above rather than
        // re-deriving the arithmetic — same range, same underlying rows.
        Assert.Equal(sales.RevenueMinorUnits, profit!.RevenueMinorUnits);
        Assert.Equal(expenses.GrandTotalMinorUnits, profit.ExpensesMinorUnits);
    }

    [Fact]
    public async Task SimulationSeed_ExportEndpoints_ReturnNonTrivialVolumeAcrossTheHistoryWindow()
    {
        using var seedClient = factory.CreateClient(); // host + simulation data already seeded via InitializeAsync().
        // Not LoginForAccessTokenAsync: this uses the factory's OWN email
        // (factory.AdminEmail), not a fresh one — TestHarness.Password is the
        // right password (SeedUserAsync always uses it), but the email must
        // match the specific Owner InitializeAsync seeded and the seeder reused.
        var loginResponse = await factory.TryLoginAsync(factory.AdminEmail, factory.AdminPassword);
        loginResponse.EnsureSuccessStatusCode();
        var tokens = await TestHarness.ReadTokensAsync(loginResponse);
        var client = factory.CreateAuthedClient(tokens.AccessToken);

        // Export ignores date ranges — it dumps the whole tenant-scoped
        // table (#95) — so these counts are the account's full row counts,
        // not a range slice.
        var dailyEntries = await client.GetAsync("/api/v1/export/daily-entries");
        dailyEntries.EnsureSuccessStatusCode();
        Assert.Equal(
            2 * SimulationSeedFactory.HistoryDays, // 2 flocks
            await CountCsvDataRowsAsync(dailyEntries));

        var salesOrders = await client.GetAsync("/api/v1/export/sales-orders");
        salesOrders.EnsureSuccessStatusCode();
        // 2 draft + 3 lifecycle confirmed + 1 recurring confirmed (#243
        // Task 3d) — multiple rows, not the single-digit-but-really-just-one
        // shape the export had before the recurring drip existed.
        Assert.Equal(6, await CountCsvDataRowsAsync(salesOrders));

        var expenses = await client.GetAsync("/api/v1/export/expenses");
        expenses.EnsureSuccessStatusCode();
        // 4 hardcoded + 1 recurring (#243 Task 3d).
        Assert.Equal(5, await CountCsvDataRowsAsync(expenses));
    }

    // Data rows only: strips the UTF-8 BOM (#95's Excel guard) and the
    // header line, same CSV shape ExportTests.cs already asserts against.
    private static async Task<int> CountCsvDataRowsAsync(HttpResponseMessage response)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var text = Encoding.UTF8.GetString(bytes.AsSpan(3)); // skip the BOM
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        return lines.Length - 1; // minus the header row
    }

    // #243 Task 3e — the completion manifest: validated row counts +
    // lifecycle-state matrix + a stable fingerprint, emitted as the LAST step
    // of SeedAsync. #279: SeedAsync's return value is now a shared SeedResult
    // (mirroring DemoDataSeeder), not the manifest — the manifest is a
    // separate artifact, written to Simulation:CredentialOutputPath
    // (SimulationSeedFactory.ConfigureWebHost points it at a temp file, same
    // as the real `seed --profile simulation` command's own manifest file),
    // so these tests read it back from disk instead of from SeedAsync's
    // return value. Calling SeedAsync() again directly (rather than only via
    // factory.CreateClient()'s own internal, return-discarding call) is
    // itself a valid idempotent re-run — same per-entity existence checks
    // that make every other seed step converge.

    private static async Task<SimulationManifest> ReadManifestAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var manifest = await JsonSerializer.DeserializeAsync<SimulationManifest>(
            stream, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return manifest ?? throw new InvalidOperationException($"Failed to deserialize manifest at {path}.");
    }

    // Recurring drip count for this fixture's shallow 12-day HistoryDays —
    // duplicated from SimulationDataSeeder's own RecurringStartDay(9)/
    // RecurringCadenceDays(7) formula, same "duplicate the private constants"
    // convention as ExpectedFeedUsageDays etc. above: (12-9)/7+1 = 1.
    private const int ExpectedRecurringPoints = 1;
    private const int ExpectedConfirmedOrders = 3 + ExpectedRecurringPoints;
    private const int ExpectedExpensesWithRecurring = 4 + ExpectedRecurringPoints;

    // #279 review Fix 2: duplicated from SimulationDataSeeder's own
    // DraftWindowDays/GradesPerDailyEntry private constants, same convention
    // as the other "duplicate the private constant" fields above — lets this
    // test assert the manifest's egg-lot and draft-entry counts EXACTLY
    // instead of the ">= 1"/">0" floor the tightened ValidateCounts no longer
    // accepts.
    private const int ExpectedDraftWindowDays = 2;
    private const int ExpectedGradesPerDailyEntry = 3;

    // #396: submit also mints a lot for each condition counter that resolved to
    // a condition grade. Kept as its own constant, mirroring the seeder's
    // ConditionLotsPerDailyEntry — the two answer different questions (grade
    // LINES vs counter-backed lots) and a single merged "5" would hide which
    // half moved if either changes.
    private const int ExpectedConditionLotsPerDailyEntry = 2;

    [Fact]
    public async Task SimulationSeed_EmitsACompleteManifestWithValidatedCountsAndLifecycleStates()
    {
        using var client = factory.CreateClient(); // host + simulation data already seeded via InitializeAsync().
        using var scope = factory.Services.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<SimulationDataSeeder>();

        var result = await seeder.SeedAsync(); // idempotent re-run.
        Assert.True(result.IsSuccess, result.Message);

        var manifest = await ReadManifestAsync(factory.ManifestPath);

        Assert.True(manifest.Complete);
        Assert.False(string.IsNullOrWhiteSpace(manifest.Fingerprint));
        Assert.Equal(SimulationDataSeeder.ManifestSchemaVersion, manifest.SchemaVersion);
        Assert.Equal(SimulationSeedFactory.HistoryDays, manifest.HistoryDays);
        // Anchor is "today" per the seeder's own IClock — within a day of the
        // real wall clock (same tolerance UtcToday-based assertions above use).
        Assert.True(Math.Abs(manifest.GeneratedAtAnchor.DayNumber - UtcToday.DayNumber) <= 1);

        var counts = manifest.Counts;
        Assert.Equal(2, counts.Accounts);
        Assert.Equal(1, counts.Owners);
        Assert.Equal(ExpectedManagers, counts.Managers);
        Assert.Equal(ExpectedSales, counts.Sales);
        Assert.Equal(ExpectedWorkers, counts.Workers);
        Assert.Equal(ExpectedReadOnly, counts.ReadOnly);
        Assert.Equal(1 + ExpectedCastUsers, counts.UsersTotal);
        Assert.Equal(2, counts.Flocks);
        Assert.Equal(2 * SimulationSeedFactory.HistoryDays, counts.DailyEntriesTotal);
        // #279 review Fix 2: one lot per grade (Large/Medium/Small) for every
        // entry that reached Submitted — Draft entries (the most recent
        // ExpectedDraftWindowDays per flock) never call SubmitDailyEntryHandler,
        // so they mint no lots. #396 adds the two condition lots (Cracked and
        // Dirty), which the seeder's own deterministic counter math keeps
        // strictly positive on every entry, so they are minted on exactly the
        // same population.
        Assert.Equal(
            (ExpectedGradesPerDailyEntry + ExpectedConditionLotsPerDailyEntry)
                * 2 * (SimulationSeedFactory.HistoryDays - ExpectedDraftWindowDays),
            counts.EggLots);
        Assert.Equal(2 + ExpectedConfirmedOrders, counts.SalesOrdersTotal);
        Assert.Equal(1, counts.Payments);
        Assert.Equal(2, counts.InventoryItems);
        Assert.Equal(2, counts.InventoryLots);
        // 2 Purchase + 2 Adjustment/Discard + one Usage row per feed usage.
        Assert.Equal(2 * ExpectedFeedUsageDays + 4, counts.InventoryMovementsTotal);
        Assert.Equal(2 * ExpectedFeedUsageDays, counts.FeedUsageRows);
        Assert.Equal(2 * ExpectedWaterUsageDays, counts.WaterUsageRows);
        Assert.Equal(2, counts.ExpenseCategories);
        Assert.Equal(ExpectedExpensesWithRecurring, counts.Expenses);

        var states = manifest.LifecycleStates;
        // Draft is stable (never sweep-dependent — always the most recent
        // ExpectedDraftWindowDays days per flock), so it's asserted exactly;
        // the Submitted/Locked split itself depends on where the farm-local
        // midnight lock-sweep boundary falls relative to the UTC seed anchor
        // at the moment this suite happens to run (#279 review Fix 2/3), so
        // it's asserted via the total reconciliation below instead of
        // hardcoding either band — SimulationDataSeeder.ValidateCounts itself
        // (which this manifest's Complete: true already proves passed) DOES
        // assert both exactly, mirroring DailyEntryLockSweep's own cutoff.
        Assert.Equal(2 * ExpectedDraftWindowDays, states.DailyEntries.Draft);
        Assert.True(states.DailyEntries.Submitted >= 1);
        Assert.True(states.DailyEntries.Locked >= 1);
        Assert.Equal(counts.DailyEntriesTotal, states.DailyEntries.Draft + states.DailyEntries.Submitted + states.DailyEntries.Locked);
        Assert.Equal(2, states.SalesOrders.Draft);
        Assert.Equal(ExpectedConfirmedOrders, states.SalesOrders.Confirmed);
        Assert.Equal(0, states.SalesOrders.Shipped + states.SalesOrders.Invoiced
                         + states.SalesOrders.Cancelled + states.SalesOrders.Voided);
        Assert.Equal(2, states.InventoryMovements.Purchase);
        Assert.Equal(2, states.InventoryMovements.Adjustment + states.InventoryMovements.Discard);
        Assert.Equal(counts.FeedUsageRows, states.InventoryMovements.Usage);
    }

    [Fact]
    public async Task SimulationSeed_Manifest_IsIdempotent_SameFingerprintAndCountsAcrossHostRestarts()
    {
        using var firstClient = factory.CreateClient(); // first host: simulation data already seeded via InitializeAsync().
        using (var firstScope = factory.Services.CreateScope())
        {
            var seeder = firstScope.ServiceProvider.GetRequiredService<SimulationDataSeeder>();
            var firstResult = await seeder.SeedAsync();
            Assert.True(firstResult.IsSuccess, firstResult.Message);
            // Fixture already seeded via InitializeAsync — this pass converges.
            Assert.Equal(SeedStatus.AlreadySeeded, firstResult.Status);
        }
        // Read back BEFORE the second run overwrites the same file.
        var firstManifest = await ReadManifestAsync(factory.ManifestPath);

        using var secondHost = factory.WithWebHostBuilder(_ => { });
        using var secondClient = secondHost.CreateClient(); // second full Program.cs run, same DB.
        using (var secondScope = secondHost.Services.CreateScope())
        {
            var seeder = secondScope.ServiceProvider.GetRequiredService<SimulationDataSeeder>();
            var secondResult = await seeder.SeedAsync();
            Assert.True(secondResult.IsSuccess, secondResult.Message);
            Assert.Equal(SeedStatus.AlreadySeeded, secondResult.Status);
        }
        var secondManifest = await ReadManifestAsync(factory.ManifestPath);

        Assert.True(secondManifest.Complete);
        Assert.Equal(firstManifest.Fingerprint, secondManifest.Fingerprint);
        Assert.Equal(firstManifest.Counts, secondManifest.Counts);
        Assert.Equal(firstManifest.LifecycleStates, secondManifest.LifecycleStates);
    }

    // #279 review Fix 3 (Agent A): ComputeFingerprint (internal, visible via
    // InternalsVisibleTo) must exclude states.DailyEntries — the
    // Draft/Submitted/Locked split depends on where the farm-local midnight
    // lock-sweep boundary falls relative to the seeder's UTC anchor, so two
    // otherwise-identical runs straddling that boundary must still
    // fingerprint the SAME. A pure unit-style check with two hand-built
    // DailyEntries splits (same total, different Submitted/Locked shape) is
    // deterministic proof of the exclusion — unlike the integration
    // idempotency test below, it doesn't depend on the suite actually running
    // near a real UTC midnight.
    [Fact]
    public void SimulationSeed_Fingerprint_ExcludesTheDailyEntryLifecycleSplit()
    {
        var counts = new SimulationManifestCounts(
            Accounts: 2, Owners: 1, Managers: 1, Sales: 1, Workers: 3, ReadOnly: 4, UsersTotal: 10,
            Flocks: 2, DailyEntriesTotal: 24, EggLots: 60, SalesOrdersTotal: 6, Payments: 1,
            InventoryItems: 2, InventoryLots: 2, InventoryMovementsTotal: 12,
            FeedUsageRows: 8, WaterUsageRows: 8, ExpenseCategories: 2, Expenses: 5);
        var states = new SimulationLifecycleStates(
            DailyEntries: new SimulationDailyEntryStates(Draft: 4, Submitted: 10, Locked: 10),
            SalesOrders: new SimulationSalesOrderStates(
                Draft: 2, Confirmed: 4, Shipped: 0, Invoiced: 0, Cancelled: 0, Voided: 0),
            InventoryMovements: new SimulationInventoryMovementStates(Purchase: 2, Usage: 8, Adjustment: 1, Discard: 1));
        // Same total (24) and every other field, but a DIFFERENT
        // Submitted/Locked split — the shape two runs straddling the
        // farm-local midnight boundary would actually produce.
        var statesWithDifferentLockSplit = states with
        {
            DailyEntries = new SimulationDailyEntryStates(Draft: 4, Submitted: 17, Locked: 3),
        };

        var fingerprint = SimulationDataSeeder.ComputeFingerprint(243, counts, states);
        var fingerprintWithDifferentLockSplit =
            SimulationDataSeeder.ComputeFingerprint(243, counts, statesWithDifferentLockSplit);
        Assert.Equal(fingerprint, fingerprintWithDifferentLockSplit);

        // Sanity check the hash isn't trivially constant: a field that DOES
        // stay in the hash (SalesOrders, never sweep/clock-dependent) still
        // changes it.
        var statesWithDifferentSalesOrders = states with
        {
            SalesOrders = new SimulationSalesOrderStates(
                Draft: 2, Confirmed: 3, Shipped: 0, Invoiced: 0, Cancelled: 0, Voided: 0),
        };
        var fingerprintWithDifferentSalesOrders =
            SimulationDataSeeder.ComputeFingerprint(243, counts, statesWithDifferentSalesOrders);
        Assert.NotEqual(fingerprint, fingerprintWithDifferentSalesOrders);
    }

    // Pure unit-style check on SimulationDataSeeder.ValidateCounts (internal,
    // visible via InternalsVisibleTo) — a hand-built "one worker short" actual
    // count against the real expectations must throw, proving the fail-closed
    // path without sabotaging a real Testcontainers seed run. Every OTHER
    // band is deliberately self-consistent (reconciliation checks pass) so
    // the one intentional mismatch is what trips ValidateCounts (#279 review
    // Fix 2's tightened, exact-count version).
    [Fact]
    public void SimulationSeed_ValidateCounts_ThrowsWhenACountIsShortOfExpectations()
    {
        var counts = new SimulationManifestCounts(
            Accounts: 2, Owners: 1, Managers: 1, Sales: 1, Workers: 2, ReadOnly: 4, UsersTotal: 9,
            Flocks: 2, DailyEntriesTotal: 24, EggLots: 60, SalesOrdersTotal: 6, Payments: 1,
            InventoryItems: 2, InventoryLots: 2, InventoryMovementsTotal: 12,
            FeedUsageRows: 8, WaterUsageRows: 8, ExpenseCategories: 2, Expenses: 5);
        var states = new SimulationLifecycleStates(
            DailyEntries: new SimulationDailyEntryStates(Draft: 4, Submitted: 10, Locked: 10),
            SalesOrders: new SimulationSalesOrderStates(
                Draft: 2, Confirmed: 4, Shipped: 0, Invoiced: 0, Cancelled: 0, Voided: 0),
            InventoryMovements: new SimulationInventoryMovementStates(Purchase: 2, Usage: 8, Adjustment: 1, Discard: 1));
        // Expects one MORE worker (and, consequently, one more total user)
        // than the counts above actually seeded — the "silently short seed"
        // this validation exists to catch.
        var expected = new SimulationExpectedCounts(
            Accounts: 2, Owners: 1, Managers: 1, Sales: 1, Workers: 3, ReadOnly: 4, UsersTotal: 10,
            Flocks: 2, DailyEntriesTotal: 24, DraftEntries: 4, SubmittedEntries: 10, LockedEntries: 10,
            EggLots: 60, SalesOrdersTotal: 6, SalesOrdersDraft: 2, SalesOrdersConfirmed: 4, Payments: 1,
            InventoryItems: 2, InventoryLots: 2, InventoryPurchaseMovements: 2,
            InventoryAdjustmentOrDiscardMovements: 2, InventoryUsageMovements: 8, InventoryMovementsTotal: 12,
            FeedUsageRows: 8, WaterUsageRows: 8, ExpenseCategories: 2, Expenses: 5);

        var ex = Assert.Throws<InvalidOperationException>(
            () => SimulationDataSeeder.ValidateCounts(counts, states, expected));
        Assert.Contains("users.workers", ex.Message);
    }

    // #500 — nothing the simulation seeder writes is unattributed.
    //
    // No BEFORE snapshot, deliberately, and worth stating so it is not "fixed"
    // into one later: this fixture owns its own Postgres container and seeds
    // once in InitializeAsync, so a [Fact] cannot observe a before state at
    // all. Nor is one needed — the only user this fixture creates outside the
    // seeder is its Owner, via TestHarness.SeedUserAsync, which calls
    // UserManager.CreateAsync directly and writes no audit row.
    //
    // An AFTER filter IS needed, which is a different thing: sibling facts in
    // this class drive real endpoints that audit (the account export), so the
    // unfiltered table mixes their rows in depending on fact order. Hence
    // SeededAuditEventsAsync.
    //
    // Two clauses because the label and the id can drift apart: a bug stamping
    // a real email beside Guid.Empty would satisfy either one alone.
    [Fact]
    public async Task SimulationSeed_LeavesNoUnattributedAuditEvent()
    {
        var rows = await SeededAuditEventsAsync();

        // A fixture that wrote no audit rows would satisfy Assert.All vacuously.
        Assert.NotEmpty(rows);
        Assert.All(rows, e =>
        {
            Assert.NotEqual("(unresolved)", e.ActorEmail);
            Assert.NotEqual(Guid.Empty, e.ActorUserId);
        });
    }

    // --- #500 slice 2: the cast are real people --------------------------

    private const string ManagerPrefix = "sim-manager-";
    private const string SalesPrefix = "sim-sales-";
    private const string WorkerPrefix = "sim-worker-";
    private const string ReadOnlyPrefix = "sim-readonly-";

    private sealed record ActorExpectation(string Describe, Func<string, bool> Matches);

    private static ActorExpectation Exactly(string email) =>
        new($"exactly {email}", actor => string.Equals(actor, email, StringComparison.Ordinal));

    private static ActorExpectation FromPool(string prefix) =>
        new($"a {prefix}* persona", actor => actor.StartsWith(prefix, StringComparison.Ordinal));

    private static ActorExpectation FromEitherPool(string a, string b) =>
        new($"a {a}* or {b}* persona",
            actor => actor.StartsWith(a, StringComparison.Ordinal)
                     || actor.StartsWith(b, StringComparison.Ordinal));

    // Exactly the rows the seed run wrote — see SimulationSeedFactory
    // .SeededAuditEventIds for why the unfiltered table is the wrong population.
    private async Task<List<AuditEvent>> SeededAuditEventsAsync()
    {
        var ids = factory.SeededAuditEventIds;
        var rows = await factory.WithTenantScopeAsync(SeedDefaults.AccountId, async db =>
            await db.AuditEvents.IgnoreQueryFilters()
                .Where(e => e.AccountId == SeedDefaults.AccountId)
                .ToListAsync());
        return rows.Where(e => ids.Contains(e.Id)).ToList();
    }

    // Every persona assertion below is true of the CONFIGURED cast, not of all
    // configurations — at Simulation:Workers=0 a correct seeder attributes the
    // eggs to a Manager instead. So pin the counts first: a config change then
    // fails here, loudly, instead of silently changing what every other
    // assertion in this section means.
    [Fact]
    public async Task SimulationSeed_CastCountsAreTheOnesThePersonaAssertionsAssume()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var emails = await db.Users
            .Where(u => u.AccountId == SeedDefaults.AccountId)
            .Select(u => u.Email!)
            .ToListAsync();

        int Count(string prefix) =>
            emails.Count(e => e.StartsWith(prefix, StringComparison.Ordinal)
                              && e.EndsWith($"@{EmailDomain}", StringComparison.Ordinal));

        Assert.Equal(ExpectedManagers, Count(ManagerPrefix));
        Assert.Equal(ExpectedSales, Count(SalesPrefix));
        Assert.Equal(ExpectedWorkers, Count(WorkerPrefix));
        Assert.Equal(ExpectedReadOnly, Count(ReadOnlyPrefix));
    }

    // Per EXACT action, never per family. A family check ("every SalesOrder.*
    // names a clerk") stays green while AddItem and Confirm carry whatever
    // actor the previous phase happened to leave resolved.
    [Fact]
    public async Task SimulationSeed_AttributesEachAuditedActionToItsPersona()
    {
        var rows = await SeededAuditEventsAsync();
        Assert.NotEmpty(rows);

        var expected = new Dictionary<string, ActorExpectation>(StringComparer.Ordinal)
        {
            // Account administration is the Owner's.
            [AuditActions.UserCreate] = Exactly(factory.AdminEmail),
            [AuditActions.UserFlockAssign] = Exactly(factory.AdminEmail),
            // Listed explicitly, and this clause is why SeedPrimaryTimeZoneAsync
            // moved ahead of RestrictOneWorkerAsync: while it followed an
            // Owner-authored phase, deleting its own ActAs(Owner) still produced
            // the Owner, so this passed by accident and pinned nothing.
            [AuditActions.AccountUpdateSettings] = Exactly(factory.AdminEmail),

            // A manager runs the farm's definitions and its spending.
            [AuditActions.FlockCreate] = FromPool(ManagerPrefix),
            [AuditActions.ProductCreate] = FromPool(ManagerPrefix),
            [AuditActions.ExpenseCreate] = FromPool(ManagerPrefix),
            [AuditActions.InventoryItemAdjust] = FromPool(ManagerPrefix),

            // The sales desk books the orders.
            [AuditActions.SalesOrderCreate] = FromPool(SalesPrefix),
            [AuditActions.SalesOrderAddItem] = FromPool(SalesPrefix),
            [AuditActions.SalesOrderConfirm] = FromPool(SalesPrefix),

            // A worker records the eggs. WHICH worker is the subject of
            // SimulationSeed_RestrictedWorkerAuthorsOnlyItsAssignedFlock — this
            // clause only pins the pool.
            [AuditActions.DailyEntryCreate] = FromPool(WorkerPrefix),
            // Either the recording worker (self-submitted) or a manager signing
            // it off — never the Owner. WHICH of the two, per entry, is
            // SimulationSeed_SubmitIsEitherTheRecordingWorkerOrAManager's job;
            // this clause only excludes everyone else.
            [AuditActions.DailyEntrySubmit] = FromEitherPool(WorkerPrefix, ManagerPrefix),
        };

        // Nothing is deferred any more: every audited action the fixture
        // produces has an expectation above. The walk below fails on any action
        // that does not, so a newly audited one cannot slip through uncovered.
        var uncovered = rows.Select(e => e.Action).Distinct(StringComparer.Ordinal)
            .Where(a => !expected.ContainsKey(a))
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToList();
        Assert.True(uncovered.Count == 0,
            "seeded audit actions with no persona expectation: " + string.Join(", ", uncovered));

        foreach (var (action, expectation) in expected)
        {
            var authored = rows.Where(e => string.Equals(e.Action, action, StringComparison.Ordinal)).ToList();
            // Without this the Assert.All below passes for an action the
            // fixture stopped producing altogether.
            Assert.True(authored.Count > 0, $"the fixture produced no {action} row to check");
            Assert.All(authored, e => Assert.True(
                expectation.Matches(e.ActorEmail),
                $"{action} was authored by {e.ActorEmail}; expected {expectation.Describe}"));
        }
    }

    [Fact]
    public async Task SimulationSeed_WritingPersonasAuthorSomething_AndReadOnlyAuthorsNothing()
    {
        var actors = (await SeededAuditEventsAsync())
            .Select(e => e.ActorEmail)
            .ToHashSet(StringComparer.Ordinal);

        // Near-vacuous at the fixture's configured pool size of 1 for both —
        // stated rather than hidden. It still fails a seeder that picks the
        // Owner everywhere.
        for (var i = 1; i <= ExpectedManagers; i++)
            Assert.Contains($"{ManagerPrefix}{i}@{EmailDomain}", actors);
        for (var i = 1; i <= ExpectedSales; i++)
            Assert.Contains($"{SalesPrefix}{i}@{EmailDomain}", actors);

        // Not near-vacuous: the pool is 3, so this fails a rotation that only
        // ever reaches one of them — including one that always picks the
        // eligible worker with the lowest index.
        for (var i = 1; i <= ExpectedWorkers; i++)
            Assert.Contains($"{WorkerPrefix}{i}@{EmailDomain}", actors);

        // ReadOnly personas exist (4 by default) and no phase makes them act.
        // Asserting the exclusion is what makes it deliberate rather than
        // forgotten (#407).
        for (var i = 1; i <= ExpectedReadOnly; i++)
            Assert.DoesNotContain($"{ReadOnlyPrefix}{i}@{EmailDomain}", actors);
    }

    // The flock-restricted worker is the one persona whose attribution is an
    // AUTHORIZATION outcome: FlockScopeGuard would refuse its write on any other
    // flock, so a rotation that ignored the restriction fails the entire seed
    // rather than misattributing quietly.
    [Fact]
    public async Task SimulationSeed_RestrictedWorkerAuthorsOnlyItsAssignedFlock()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Identify the pair by JOINING the assignment row, never by indexing a
        // flock list — Postgres returns rows unordered, so a list index is not a
        // stable identity for the restricted flock.
        var assignment = await db.UserRoleAssignments.IgnoreQueryFilters()
            .Where(a => a.AccountId == SeedDefaults.AccountId && a.FlockId != null)
            .SingleAsync();
        var restrictedFlockId = assignment.FlockId!.Value;

        var flockOfEntry = await db.DailyEntries.IgnoreQueryFilters()
            .Where(e => e.AccountId == SeedDefaults.AccountId)
            .ToDictionaryAsync(e => e.Id, e => e.FlockId);

        var creates = (await SeededAuditEventsAsync())
            .Where(e => string.Equals(e.Action, AuditActions.DailyEntryCreate, StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(creates);

        var byRestrictedWorker = creates.Where(e => e.ActorUserId == assignment.UserId).ToList();

        // The positive clause. Without it this passes vacuously for an
        // implementation where the restricted worker never records anything.
        Assert.NotEmpty(byRestrictedWorker);
        Assert.All(byRestrictedWorker, e => Assert.Equal(restrictedFlockId, flockOfEntry[e.EntityId]));
    }

    // #606 — trusted simulation provisioning (RestrictOneWorkerAsync) must
    // preserve the SAME audit shape the interactive AssignFlockHandler wrote:
    // Owner actor, the worker's real email, and the assigned flock's real
    // NAME (never an id or a hardcoded literal). Actor-only coverage above
    // (SimulationSeed_AttributesEachAuditedActionToItsPersona /
    // SimulationSeed_EachActionsActorHoldsTheExpectedRoles) is insufficient —
    // only deserializing Details catches a corrupted or ID-based value.
    [Fact]
    public async Task SimulationSeed_RestrictedWorkerAssignment_PreservesActorAndAuditDetails()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var assignment = await db.UserRoleAssignments.IgnoreQueryFilters()
            .Where(a => a.AccountId == SeedDefaults.AccountId && a.FlockId != null)
            .SingleAsync();
        var flock = await db.Flocks.IgnoreQueryFilters()
            .SingleAsync(f => f.Id == assignment.FlockId);
        var worker = await db.Users.IgnoreQueryFilters()
            .SingleAsync(u => u.Id == assignment.UserId);

        // The established fixture topology (SimulationDataSeeder's Task-2
        // flock list): the restricted worker's flock is always the
        // first-created one, "Sim House A" — pinned separately from the
        // "compare against the actual entity" assertions below so a seeder
        // that assigned the WRONG flock (but still reported its real name)
        // cannot pass by comparing a value against itself.
        Assert.Equal("Sim House A", flock.Name);

        var flockAssignRows = await db.AuditEvents.IgnoreQueryFilters()
            .Where(e => e.AccountId == SeedDefaults.AccountId
                        && e.Action == AuditActions.UserFlockAssign
                        && e.EntityId == worker.Id)
            .ToListAsync();
        var row = Assert.Single(flockAssignRows);
        Assert.Equal(factory.AdminEmail, row.ActorEmail);

        // AuditWriter serializes details with JsonSerializerDefaults.Web —
        // camelCase property names.
        using var details = JsonDocument.Parse(row.DetailsJson!);
        Assert.Equal(worker.Email, details.RootElement.GetProperty("email").GetString());
        Assert.Equal(flock.Name, details.RootElement.GetProperty("flock").GetString());

        // Rerunning the seeder remains convergent: no duplicate assignment
        // row or audit event for the same restricted pair.
        var rerun = await scope.ServiceProvider.GetRequiredService<SimulationDataSeeder>().SeedAsync();
        Assert.Equal(SeedStatus.AlreadySeeded, rerun.Status);

        var assignmentsAfter = await db.UserRoleAssignments.IgnoreQueryFilters()
            .Where(a => a.AccountId == SeedDefaults.AccountId && a.FlockId != null)
            .CountAsync();
        Assert.Equal(1, assignmentsAfter);
        var eventsAfter = await db.AuditEvents.IgnoreQueryFilters()
            .Where(e => e.AccountId == SeedDefaults.AccountId
                        && e.Action == AuditActions.UserFlockAssign
                        && e.EntityId == worker.Id)
            .CountAsync();
        Assert.Equal(1, eventsAfter);
    }

    // --- #500 slice 4: both provenance shapes ----------------------------

    // Per ENTRY, which the persona map above cannot express: it checks the
    // submitter is a worker or a manager, which stays true when every entry is
    // submitted by a worker who did not record it — a shape no farm has.
    [Fact]
    public async Task SimulationSeed_SubmitIsEitherTheRecordingWorkerOrAManager()
    {
        var rows = await SeededAuditEventsAsync();
        var creatorOf = rows
            .Where(e => string.Equals(e.Action, AuditActions.DailyEntryCreate, StringComparison.Ordinal))
            .ToDictionary(e => e.EntityId, e => e.ActorEmail);
        var submits = rows
            .Where(e => string.Equals(e.Action, AuditActions.DailyEntrySubmit, StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(submits);

        var selfSubmitted = 0;
        var managerSubmitted = 0;
        foreach (var submit in submits)
        {
            var creator = creatorOf[submit.EntityId];
            if (string.Equals(submit.ActorEmail, creator, StringComparison.Ordinal))
            {
                selfSubmitted++;
                continue;
            }

            // Not the recorder, so it must be a manager — and in particular
            // never the Owner. Replacing the manager branch with plain
            // ActAs(Owner) is caught by exactly this: "the two actors differ"
            // would still be satisfied.
            Assert.StartsWith(ManagerPrefix, submit.ActorEmail, StringComparison.Ordinal);
            Assert.NotEqual(factory.AdminEmail, submit.ActorEmail);
            managerSubmitted++;
        }

        // Both kinds must exist. Either clause alone passes for a fixture that
        // mixed nothing.
        Assert.True(selfSubmitted > 0, "no entry was submitted by the worker who recorded it");
        Assert.True(managerSubmitted > 0, "no entry was submitted by a manager");
    }

    // The #494 shape no fixture exercised before: a record whose creator and
    // last-changer are different people. Stated separately from the test above
    // because it is a claim about what the SCREENS can show, not about who is
    // allowed to submit.
    [Fact]
    public async Task SimulationSeed_CarriesBothProvenanceShapes()
    {
        var rows = await SeededAuditEventsAsync();
        var creatorOf = rows
            .Where(e => string.Equals(e.Action, AuditActions.DailyEntryCreate, StringComparison.Ordinal))
            .ToDictionary(e => e.EntityId, e => e.ActorUserId);

        var pairs = rows
            .Where(e => string.Equals(e.Action, AuditActions.DailyEntrySubmit, StringComparison.Ordinal))
            .Select(e => (Creator: creatorOf[e.EntityId], Submitter: e.ActorUserId))
            .ToList();
        Assert.NotEmpty(pairs);

        Assert.Contains(pairs, p => p.Creator == p.Submitter);
        Assert.Contains(pairs, p => p.Creator != p.Submitter);
    }

    // --- #500 slice 5: the remaining guards ------------------------------

    // ActorUserId and ActorEmail must resolve to the SAME user. Asserting the
    // two independently — as every other test here does — passes for a row that
    // pairs one person's id with another's email.
    //
    // What such a mismatch actually costs: the id never reaches the UI
    // (EntityProvenance carries only the emails), but
    // AuditEventRepository.GetProvenanceChunkAsync's creator and
    // self-promotion-exclusion SQL compares ActorUserId — so a mismatch makes it
    // select the WRONG ROW, whose email is then displayed as authoritative.
    [Fact]
    public async Task SimulationSeed_ActorIdentityIsInternallyConsistent()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var emailOfUser = await db.Users
            .Where(u => u.AccountId == SeedDefaults.AccountId)
            .ToDictionaryAsync(u => u.Id, u => u.Email!);

        var rows = await SeededAuditEventsAsync();
        Assert.NotEmpty(rows);

        Assert.All(rows, e =>
        {
            Assert.True(emailOfUser.ContainsKey(e.ActorUserId),
                $"{e.Action} names actor id {e.ActorUserId}, which is no user in this account");
            Assert.Equal(emailOfUser[e.ActorUserId], e.ActorEmail);
        });
    }

    // The stored ROLES per action, which the email prefixes above cannot see: a
    // persona named into the right prefix but created with the wrong role would
    // satisfy every other test here and still change what FlockScopeGuard
    // permits.
    //
    // Stated per action rather than as one "holds the role the action implies"
    // rule, because that rule is unsatisfiable: workers deliberately carry NO
    // role row ("Worker" is a pseudo-role the seeder's storedRole conversion maps to null), and
    // DailyEntry.Submit is either a role-less worker or a Manager.
    [Fact]
    public async Task SimulationSeed_EachActionsActorHoldsTheExpectedRoles()
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var accountUsers = await db.Users.Where(u => u.AccountId == SeedDefaults.AccountId).ToListAsync();
        var rolesOf = new Dictionary<Guid, IReadOnlyList<string>>();
        foreach (var u in accountUsers) rolesOf[u.Id] = [.. await users.GetRolesAsync(u)];

        var rows = await SeededAuditEventsAsync();

        var expectedRole = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [AuditActions.UserCreate] = Roles.Owner,
            [AuditActions.UserFlockAssign] = Roles.Owner,
            [AuditActions.AccountUpdateSettings] = Roles.Owner,
            [AuditActions.FlockCreate] = Roles.Manager,
            [AuditActions.ProductCreate] = Roles.Manager,
            [AuditActions.ExpenseCreate] = Roles.Manager,
            [AuditActions.InventoryItemAdjust] = Roles.Manager,
            [AuditActions.SalesOrderCreate] = Roles.Sales,
            [AuditActions.SalesOrderAddItem] = Roles.Sales,
            [AuditActions.SalesOrderConfirm] = Roles.Sales,
            // null = the actor must hold NO stored role at all.
            [AuditActions.DailyEntryCreate] = null,
        };

        foreach (var (action, role) in expectedRole)
        {
            var authored = rows.Where(e => string.Equals(e.Action, action, StringComparison.Ordinal)).ToList();
            Assert.True(authored.Count > 0, $"the fixture produced no {action} row to check");
            Assert.All(authored, e =>
            {
                var held = rolesOf[e.ActorUserId];
                if (role is null) Assert.Empty(held);
                else Assert.Contains(role, held);
            });
        }

        // Submit is the split case: the recording worker (no roles) or a
        // manager. Never the Owner — the clause that fails when the manager
        // branch is replaced by ActAs(Owner).
        var creatorOf = rows
            .Where(e => string.Equals(e.Action, AuditActions.DailyEntryCreate, StringComparison.Ordinal))
            .ToDictionary(e => e.EntityId, e => e.ActorUserId);
        var submits = rows
            .Where(e => string.Equals(e.Action, AuditActions.DailyEntrySubmit, StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(submits);
        Assert.All(submits, e =>
        {
            var held = rolesOf[e.ActorUserId];
            Assert.DoesNotContain(Roles.Owner, held);
            if (e.ActorUserId == creatorOf[e.EntityId]) Assert.Empty(held);
            else Assert.Contains(Roles.Manager, held);
        });
    }

    // The only assertion here that fails for a Pick returning ANY constant
    // element, or a random one. Three things it has to get right:
    //
    //  - WHICH flock. The restricted one is identified by joining
    //    UserRoleAssignments.FlockId, never by list index: Postgres returns rows
    //    unordered, so an index is not a stable identity.
    //  - WHICH count. Eligible is 3 on the restricted flock and 2 on every
    //    other, so a single pool size is wrong for one of them.
    //  - HOW MANY samples. Across the FULL seeded history, not one cycle: a
    //    random Pick then survives with probability (1/3)^n over n entries
    //    rather than the ~1.2% a four-day check would allow. A probabilistic
    //    kill, not a certain one — stated, not claimed away.
    [Fact]
    public async Task SimulationSeed_WorkerRotationIsExactAndDeterministic()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var assignment = await db.UserRoleAssignments.IgnoreQueryFilters()
            .Where(a => a.AccountId == SeedDefaults.AccountId && a.FlockId != null)
            .SingleAsync();
        var restrictedFlockId = assignment.FlockId!.Value;

        var anchor = (await db.SimulationSeedStates.IgnoreQueryFilters()
            .SingleAsync(s => s.AccountId == SeedDefaults.AccountId)).Anchor;

        // Creation order, which is what SeedCastAsync's loop produces and
        // therefore what Pick indexes into. Ordering by email reproduces it only
        // while the pool is single-digit — `sim-worker-10` sorts before
        // `sim-worker-2`. Safe at the configured 3, and
        // SimulationSeed_CastCountsAreTheOnesThePersonaAssertionsAssume fails
        // first on any config change that would break it.
        var workers = await db.Users
            .Where(u => u.AccountId == SeedDefaults.AccountId && u.Email!.StartsWith(WorkerPrefix))
            .OrderBy(u => u.Email)
            .Select(u => u.Id)
            .ToListAsync();
        Assert.Equal(ExpectedWorkers, workers.Count);

        var entries = await db.DailyEntries.IgnoreQueryFilters()
            .Where(e => e.AccountId == SeedDefaults.AccountId)
            .Select(e => new { e.Id, e.FlockId, e.Date })
            .ToListAsync();

        var actorOf = (await SeededAuditEventsAsync())
            .Where(e => string.Equals(e.Action, AuditActions.DailyEntryCreate, StringComparison.Ordinal))
            .ToDictionary(e => e.EntityId, e => e.ActorUserId);

        Assert.Equal(entries.Count, actorOf.Count);

        var checkedRestricted = 0;
        var checkedOther = 0;
        foreach (var entry in entries)
        {
            var onRestrictedFlock = entry.FlockId == restrictedFlockId;
            var eligible = onRestrictedFlock
                ? workers
                : workers.Where(w => w != assignment.UserId).ToList();
            Assert.Equal(onRestrictedFlock ? ExpectedWorkers : ExpectedWorkers - 1, eligible.Count);

            // The seeder's own index: it walks d = 1..historyDays writing
            // today.AddDays(-d), so d is the day's distance from the anchor.
            var dayIndex = anchor.DayNumber - entry.Date.DayNumber;
            Assert.Equal(eligible[dayIndex % eligible.Count], actorOf[entry.Id]);

            if (onRestrictedFlock) checkedRestricted++; else checkedOther++;
        }

        // Both flocks were actually exercised — otherwise the differing-count
        // clause above is vacuous for whichever one is missing.
        Assert.True(checkedRestricted > 0 && checkedOther > 0,
            $"expected entries on both flocks; got {checkedRestricted} restricted, {checkedOther} other");
    }
}
