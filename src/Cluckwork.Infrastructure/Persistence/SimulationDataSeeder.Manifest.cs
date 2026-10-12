using Cluckwork.Domain.Modules.Farm.Contracts;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Cluckwork.Infrastructure.Persistence;

public sealed partial class SimulationDataSeeder
{
    private async Task<SimulationManifest> EmitManifestAsync(
        Guid accountId, DateOnly today, SimulationOptions sim, CancellationToken ct)
    {
        var (counts, states) = await ComputeCountsAsync(accountId, ct);
        // Same farm-local "today" lookup DailyEntryLockSweep itself just used
        // (DrainLockSweepAsync already ran it to exhaustion, in
        // SeedAsync — #638) — recomputing
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
        var usersTotal = await access.CountUsersAsync(accountId, ct);
        var ownerCount = (await access.ListUsersInRoleAsync(accountId, Roles.Owner, ct)).Count;
        var managerCount = (await access.ListUsersInRoleAsync(accountId, Roles.Manager, ct)).Count;
        var salesCount = (await access.ListUsersInRoleAsync(accountId, Roles.Sales, ct)).Count;
        var readOnlyCount = (await access.ListUsersInRoleAsync(accountId, Roles.ReadOnly, ct)).Count;
        // Workers deliberately carry no role row (SeedCastAsync) — derive by
        // subtraction rather than a role lookup that would always find none.
        var workerCount = usersTotal - ownerCount - managerCount - salesCount - readOnlyCount;

        var accountCount = await farmFixture.CountAccountsAsync(ct);
        // #627 — the flock lifecycle split the manifest now certifies exactly
        // (INV-5). Every count but the accounts one is tenant-filtered.
        var flockCounts = await flockFixture.CountAsync(ct);
        var eggCounts = await eggsFixture.CountAsync(ct);
        var commerceCounts = await commerceFixture.CountAsync(ct);
        var inventoryCounts = await inventoryFixture.CountAsync(ct);
        var financeCounts = await financeFixture.CountAsync(ct);

        var counts = new SimulationManifestCounts(
            Accounts: accountCount,
            Customers: commerceCounts.Customers,
            BirdMovements: flockCounts.BirdMovements,
            Owners: ownerCount,
            Managers: managerCount,
            Sales: salesCount,
            Workers: workerCount,
            ReadOnly: readOnlyCount,
            UsersTotal: usersTotal,
            Flocks: flockCounts.Flocks,
            DailyEntriesTotal: eggCounts.DailyEntries,
            EggLots: eggCounts.EggLots,
            SalesOrdersTotal: commerceCounts.SalesOrders,
            Payments: commerceCounts.Payments,
            InventoryItems: inventoryCounts.Items,
            InventoryLots: inventoryCounts.Lots,
            InventoryMovementsTotal: inventoryCounts.Movements,
            FeedUsageRows: inventoryCounts.FeedUsages,
            WaterUsageRows: inventoryCounts.WaterUsages,
            ExpenseCategories: financeCounts.ExpenseCategories,
            Expenses: financeCounts.Expenses);

        var states = new SimulationLifecycleStates(
            DailyEntries: new SimulationDailyEntryStates(
                eggCounts.DraftEntries, eggCounts.SubmittedEntries, eggCounts.LockedEntries),
            Flocks: new SimulationFlockStates(
                flockCounts.ActiveFlocks, flockCounts.DepletedFlocks, flockCounts.ArchivedFlocks),
            SalesOrders: new SimulationSalesOrderStates(
                commerceCounts.DraftOrders, commerceCounts.ConfirmedOrders, commerceCounts.ShippedOrders,
                commerceCounts.InvoicedOrders, commerceCounts.CancelledOrders, commerceCounts.VoidedOrders),
            InventoryMovements: new SimulationInventoryMovementStates(
                inventoryCounts.PurchaseMovements, inventoryCounts.UsageMovements,
                inventoryCounts.AdjustmentMovements, inventoryCounts.DiscardMovements));

        return (counts, states);
    }

    // Pure function of SimulationOptions plus the two plain DateOnly anchors
    // (today, farmToday) — still no DB access, so a unit test can call it
    // with hand-built inputs too. See the class-level comment at the top of
    // the ManifestExpectations partial for why every field here is DERIVED rather than a
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
        //
        // #627 — plus the feed lot's explicit adjustment band (the bedding
        // item is untouched by it): the feed item lands on exactly 10 +
        // FeedAdjustmentBandCount = 120 movements, the bedding item keeps its
        // 2, and the account-wide total below is 122 at the fixture scale.
        var inventoryMovementsTotal =
            InventoryItemsWanted.Length + InventoryItemsWanted.Length
            + FeedAdjustmentBandCount + feedUsageRows;

        return new SimulationExpectedCounts(
            Accounts: 2, // primary + SecondAccountId — never a third on a re-run.
            Owners: 1, // the reused seeded admin — this seeder never creates a second Owner.
            Managers: sim.Managers,
            Sales: sim.Sales,
            Workers: sim.Workers,
            ReadOnly: sim.ReadOnly,
            UsersTotal: 1 + sim.Managers + sim.Sales + sim.Workers + sim.ReadOnly,
            // #627 — two operational houses + the exact picker catalog.
            Flocks: FlockTopologyCount + CatalogFlockBandCount,
            FlocksActive: FlockTopologyCount + NumberedCatalogFlockCount + 1,
            FlocksDepleted: 1,
            FlocksArchived: 1,
            Customers: CustomersWanted.Length + CustomerBandCount,
            // #627 — automatic mortality baseline (explicit day loop, per
            // flock) plus the explicit adjustment band on the first
            // operational flock. No other flock carries explicit rows, and no
            // cull row is ever seeded, so this is the account-wide total.
            BirdMovements: FlockTopologyCount * AutomaticMortalityPerFlock(historyDays)
                + ExplicitBirdMovementsCount,
            DailyEntriesTotal: dailyEntriesTotal,
            DraftEntries: draftEntries,
            SubmittedEntries: submittedEntries,
            LockedEntries: lockedEntries,
            EggLots: (GradesPerDailyEntry + ConditionLotsPerDailyEntry) * submittedOrLockedEntries,
            SalesOrdersTotal: DraftOrdersCount + salesOrdersConfirmed,
            SalesOrdersDraft: DraftOrdersCount,
            SalesOrdersConfirmed: salesOrdersConfirmed,
            Payments: 1, // exactly one partial payment (EnsurePartialPaymentAsync).
            InventoryItems: InventoryItemsWanted.Length,
            InventoryLots: InventoryItemsWanted.Length, // one opening lot per item.
            InventoryPurchaseMovements: InventoryItemsWanted.Length,
            InventoryAdjustmentOrDiscardMovements: InventoryItemsWanted.Length
                + FeedAdjustmentBandCount, // one discard + the feed adjustment band
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
        // #627 — the flock lifecycle split, exact (INV-5) plus a reconciliation
        // sum so the three bands cannot hide a fourth status the counts don't
        // name.
        Check(states.Flocks.Active == expected.FlocksActive,
            $"flocks.active: expected {expected.FlocksActive}, got {states.Flocks.Active}");
        Check(states.Flocks.Depleted == expected.FlocksDepleted,
            $"flocks.depleted: expected {expected.FlocksDepleted}, got {states.Flocks.Depleted}");
        Check(states.Flocks.Archived == expected.FlocksArchived,
            $"flocks.archived: expected {expected.FlocksArchived}, got {states.Flocks.Archived}");
        var flockStateSum = states.Flocks.Active + states.Flocks.Depleted + states.Flocks.Archived;
        Check(flockStateSum == counts.Flocks,
            $"flocks reconciliation: active+depleted+archived ({flockStateSum}) != total ({counts.Flocks})");
        Check(counts.Customers == expected.Customers,
            $"customers: expected {expected.Customers}, got {counts.Customers}");
        Check(counts.BirdMovements == expected.BirdMovements,
            $"birdMovements: expected {expected.BirdMovements}, got {counts.BirdMovements}");
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
        // #627 — states.Flocks joins the canonical payload: nothing time-based
        // moves a flock between Active/Depleted/Archived after the seeder
        // writes it, so the split is exactly as stable as the other included
        // states. states.DailyEntries stays deliberately EXCLUDED (the
        // sweep-boundary flake the comment above documents).
        var canonical = JsonSerializer.Serialize(
            new { seed, counts, states.SalesOrders, states.InventoryMovements, states.Flocks }, FingerprintJsonOptions);
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

public sealed record SimulationFlockStates(int Active, int Depleted, int Archived);

public sealed record SimulationLifecycleStates(
    SimulationDailyEntryStates DailyEntries,
    SimulationSalesOrderStates SalesOrders,
    SimulationInventoryMovementStates InventoryMovements,
    SimulationFlockStates Flocks);

public sealed record SimulationManifestCounts(
    int Accounts,
    int Customers,
    int BirdMovements,
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
    int FlocksActive,
    int FlocksDepleted,
    int FlocksArchived,
    int Customers,
    int BirdMovements,
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
