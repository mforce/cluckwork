using Cluckwork.Application.Modules.Finance.Contracts;
using Cluckwork.Application.Modules.GeneralInventory.Contracts;

namespace Cluckwork.Infrastructure.Persistence;

public sealed partial class SimulationDataSeeder
{
    // --- Inventory: items, opening purchases, adjustments, feed/water usage,
    // expenses (#243 Task 3c) --------------------------------------------
    //
    // Same real-handler-reuse discipline as sales in the Sales partial (CreateInventoryItem/
    // RecordPurchase/RecordAdjustment/RecordFeedUsage/RecordWaterUsage/
    // CreateExpenseCategory/CreateExpense) so reports/exports built against
    // this data see the same shapes a user clicking through Inventory/
    // Expenses would produce. Idempotency: items/categories by Name
    // (EnsureFlockAsync-style); the opening purchase by "does the item
    // already have a lot" (HasLotsAsync — this seeder only ever creates ONE
    // opening lot per item); the adjustment by "does the lot already carry a
    // correction movement"; usage/expense rows by the (flock/item, date) or
    // Description natural key this seeder controls, same convention as
    // SeedFlockHistoryAsync's natural-key skip and FindOrderAsync in the ProductionHistory and Sales partials.

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
        Guid accountId, DateOnly today, IReadOnlyList<Guid> flockIds, SimCast cast, SimulationOptions sim,
        CancellationToken ct)
    {
        // #500 — stores and feeding are a manager's job. Of everything below only
        // InventoryItem.Adjust and Expense.Create write an audit row, but each
        // helper takes its actor explicitly anyway: the caller owning the choice
        // is what keeps a later reorder from silently changing attribution.
        // Manager also matters functionally here — feed/water usage go through
        // FlockScopeGuard, which passes a Manager by role.
        var storeKeeper = Pick(cast.Managers, 0, cast.Owner, "Managers");
        var (feedItemId, feedLotId, beddingItemId, beddingLotId) =
            await SeedInventoryItemsAndOpeningPurchasesAsync(accountId, today, cast, sim, ct);

        await SeedFeedUsageAsync(accountId, today, flockIds, feedItemId, storeKeeper, ct);

        // The two original correction rows, kept on opening + 2 (see the
        // opening-lookback note in SeedInventoryItemsAndOpeningPurchasesAsync).
        // They run AFTER the feed usages for the same domain reason as the
        // adjustment band below: a discard/correction is legal only once the
        // lot has been drawn down below its received quantity.
        var adjustmentDate = today.AddDays(-(InventoryOpeningLookbackDays(sim) - 2));
        await EnsureAdjustmentAsync(
            accountId, feedItemId, feedLotId, adjustmentDate, "Discard",
            FeedDiscardQuantity, "Simulation fixture: torn bag spoiled in storage", storeKeeper, ct);
        await EnsureAdjustmentAsync(
            accountId, beddingItemId, beddingLotId, adjustmentDate, "Adjustment",
            BeddingAdjustmentQuantity, "Simulation fixture: recount shrinkage", storeKeeper, ct);

        // #627 — the over-cap band, after the usages too (same rule).
        await SeedFeedAdjustmentsAsync(accountId, today, feedItemId, feedLotId, storeKeeper, sim, ct);

        await SeedWaterUsageAsync(accountId, today, flockIds, storeKeeper, ct);
        await SeedExpensesAsync(accountId, today, flockIds, cast, sim, ct);
    }

    // #627 — the over-cap inventory band: exactly 110 date-distinct +1
    // Adjustment rows on the feed lot's OPENING lot. The item ends with ten
    // other movements (one purchase, one discard, eight usages), so the band
    // reaches exactly 120 on the feed item while the bedding item keeps its
    // two, making the account-wide total exactly 122.
    //
    // Seeded AFTER the feed usages (which draw the lot down below its received
    // quantity), never before: InventoryLot.Adjust refuses a correction that
    // would leave a lot at or above what was received. The movement COUNT is
    // order-independent (the ledger is append-only), so the final band is the
    // same either way — only the legal order of the writes differs.
    //
    // Natural key (InventoryLotId, Date), never the free-text reason. Dates
    // enumerate forward from opening + 1, skipping the existing discard at
    // opening + 2, and stop after 110 rows — all on/after the lot's receipt
    // and at least six days before the anchor. The oldest added row carries a
    // stable sentinel reason so the SPA ledger spec can prove its second
    // page renders; the reason is DATA, not identity.
    private const int FeedAdjustmentCount = 110;
    private const string FeedAdjustmentPageTwoSentinelReason = "Sim feed adjustment page two sentinel";

    // Mirrors the openingDate note in
    // SeedInventoryItemsAndOpeningPurchasesAsync — the two floors (the +5
    // margin and the 117-day #627 floor) are one decision and live next to
    // each other there.
    private static int InventoryOpeningLookbackDays(SimulationOptions sim) =>
        Math.Max(EffectiveHistoryDays(sim) + 5, 117);

    private async Task SeedFeedAdjustmentsAsync(
        Guid accountId, DateOnly today, Guid feedItemId, Guid feedLotId, SimActor actor,
        SimulationOptions sim, CancellationToken ct)
    {
        ActAs(actor);

        var added = 0;
        for (var d = 1; d <= InventoryOpeningLookbackDays(sim) - 1 && added < FeedAdjustmentCount; d++)
        {
            if (d == 2) continue; // the existing discard owns opening + 2
            var date = today.AddDays(-(InventoryOpeningLookbackDays(sim) - d));
            var isOldest = added == 0; // first date enumerated == oldest == the sentinel
            var reason = isOldest
                ? FeedAdjustmentPageTwoSentinelReason
                : $"Simulation fixture feed adjustment {added + 1}";

            var exists = await inventoryFixture.FeedLotMovementExistsAsync(feedLotId, date, ct);
            if (!exists)
            {
                var result = await inventory.RecordAdjustmentAsync(new RecordAdjustmentCommand(
                    feedItemId, feedLotId, date, "Adjustment", 1m, reason), accountId, ct);
                Require(result, $"record feed adjustment for lot {feedLotId} on {date:yyyy-MM-dd}");
            }
            added++;
        }

        // Fail closed rather than silently publish a short band (the probe
        // above only skips rows that already exist, so shortness means the
        // dates stopped being distinct).
        var count = await inventoryFixture.CountFeedLotAdjustmentsAsync(feedLotId, ct);
        if (count < FeedAdjustmentCount)
            throw new InvalidOperationException(
                $"Simulation seed: only {count} feed-lot adjustment rows exist; the fixture " +
                $"expects exactly {FeedAdjustmentCount} date-distinct rows.");
    }

    // #627 — extracted (behavior-identical) from SeedInventoryOperationsAsync
    // so the phase owns the item + opening-lot ids its callers (the band,
    // the correction rows, the usage loops) all need: the band's +1 rows and
    // the original discard/correction rows are all LOT CHANGES, and
    // InventoryLot.Adjust's domain rule (AdjustExceedsReceived) refuses any
    // change that would leave the lot at or above its received quantity —
    // so every one of them is legal only once the feed usages have drawn the
    // lot down. SeedInventoryOperationsAsync therefore seeds the usages
    // BEFORE those rows; the append-only ledger makes the movement COUNT
    // order-independent, so the certified bands are the same either way. The
    // opening purchase itself still precedes every usage, as before.
    private async Task<(Guid FeedItemId, Guid FeedLotId, Guid BeddingItemId, Guid BeddingLotId)>
        SeedInventoryItemsAndOpeningPurchasesAsync(
        Guid accountId, DateOnly today, SimCast cast, SimulationOptions sim, CancellationToken ct)
    {
        var storeKeeper = Pick(cast.Managers, 0, cast.Owner, "Managers");

        // Received well before any usage date so FIFO always finds it on-hand
        // as-of any usage day — same "older than everything it must cover"
        // shape as the flock placement dates in SeedFlockTopologyAsync.
        //
        // #627 — the feed lot's 110 deterministic adjustments need 116 distinct
        // days of room on/after receipt (opening + 1 through opening + 116,
        // minus the existing discard at opening + 2), so the opening lookback is
        // floored at 117: the latest added date then lands at anchor day - 6,
        // clear of the four recent feed-usage dates and any farm-today skew that
        // trails the UTC anchor. #627 intentionally moves the 90-day default
        // opening from 95 to 117 — the band needs the floor, not the history
        // margin.
        //
        // The lot's received quantity (3000) is NOT sized for the band: the
        // band's legality is ORDER, not headroom — the lot starts its life AT
        // its received quantity, where InventoryLot.Adjust refuses even one +1
        // (AdjustExceedsReceived), so the 144 kg of usages and the -15 discard
        // must run BEFORE the 110 +1 rows, which then land at 2951 < 3000.
        var openingDate = today.AddDays(-InventoryOpeningLookbackDays(sim));

        var itemIds = new Dictionary<string, Guid>();
        foreach (var (name, category, unit, cost) in InventoryItemsWanted)
            itemIds[name] = await EnsureInventoryItemAsync(accountId, name, category, unit, cost, storeKeeper, ct);
        var feedItemId = itemIds["Sim Layer Feed"];
        var beddingItemId = itemIds["Sim Pine Shavings"];

        var feedLotId = await EnsureOpeningPurchaseAsync(
            accountId, feedItemId, openingDate, FeedOpeningPurchaseQuantity, storeKeeper, ct);
        var beddingLotId = await EnsureOpeningPurchaseAsync(
            accountId, beddingItemId, openingDate, BeddingOpeningPurchaseQuantity, storeKeeper, ct);

        return (feedItemId, feedLotId, beddingItemId, beddingLotId);
    }

    private async Task<Guid> EnsureInventoryItemAsync(
        Guid accountId, string name, string category, string unit, long defaultUnitCostMinorUnits,
        SimActor actor, CancellationToken ct)
    {
        if (await inventoryFixture.FindItemIdByNameAsync(name, ct) is Guid existingId) return existingId;

        ActAs(actor);
        var result = await inventory.CreateItemAsync(
            new CreateInventoryItemCommand(name, category, unit, defaultUnitCostMinorUnits), accountId, ct);
        Require(result, $"create inventory item {name}");
        return result.Value;
    }

    private async Task<Guid> EnsureOpeningPurchaseAsync(
        Guid accountId, Guid itemId, DateOnly receivedDate, decimal quantity, SimActor actor,
        CancellationToken ct)
    {
        // The lot check doubles as the idempotency probe: this seeder only
        // ever creates ONE opening lot per item, so "any lot exists" is
        // equivalent to "the opening purchase already ran".
        if (await inventoryFixture.FindLotIdAsync(itemId, ct) is Guid existingLotId) return existingLotId;

        ActAs(actor);
        // UnitCostMinorUnits omitted: falls back to the item's default cost
        // set above.
        var result = await inventory.RecordPurchaseAsync(new RecordPurchaseCommand(
            itemId, receivedDate, quantity, UnitCostMinorUnits: null, LotNumber: "SIM-OPEN-1",
            ExpiryDate: null, Note: "Simulation fixture opening stock"), accountId, ct);
        Require(result, $"record opening purchase for inventory item {itemId}");
        return result.Value;
    }

    private async Task EnsureAdjustmentAsync(
        Guid accountId, Guid itemId, Guid lotId, DateOnly date, string type, decimal quantityDelta, string reason,
        SimActor actor, CancellationToken ct)
    {
        if (await inventoryFixture.HasAdjustmentOrDiscardAsync(lotId, ct)) return;

        ActAs(actor);
        var result = await inventory.RecordAdjustmentAsync(new RecordAdjustmentCommand(
            itemId, lotId, date, type, quantityDelta, reason), accountId, ct);
        Require(result, $"record inventory adjustment for lot {lotId}");
    }

    private async Task SeedFeedUsageAsync(
        Guid accountId, DateOnly today, IReadOnlyList<Guid> flockIds, Guid feedItemId, SimActor actor,
        CancellationToken ct)
    {
        // RecordFeedUsageHandler goes through FlockScopeGuard — the actor must be
        // one the guard lets past for BOTH flocks, which a Manager is by role.
        ActAs(actor);

        foreach (var flockId in flockIds)
            for (var d = 1; d <= FeedUsageDays; d++)
            {
                // Day 1+ past, matching the daily-entry-history convention
                // in the ProductionHistory partial (day-of-anchor could spuriously read as "future"
                // against the farm's own, timezone-skewed today).
                var date = today.AddDays(-d);
                if (await inventoryFixture.FeedUsageExistsAsync(flockId, feedItemId, date, ct)) continue;

                var result = await inventory.RecordFeedUsageAsync(new RecordFeedUsageCommand(
                    flockId, feedItemId, date, FeedUsagePerFlockPerDay,
                    "Simulation fixture daily feeding"), accountId, ct);
                Require(result, $"record feed usage for flock {flockId} on {date:yyyy-MM-dd}");
            }
    }

    private async Task SeedWaterUsageAsync(
        Guid accountId, DateOnly today, IReadOnlyList<Guid> flockIds, SimActor actor, CancellationToken ct)
    {
        // Flock-scoped, same as feed usage above.
        ActAs(actor);

        foreach (var flockId in flockIds)
            for (var d = 1; d <= WaterUsageDays; d++)
            {
                var date = today.AddDays(-d);
                if (await inventoryFixture.WaterUsageExistsAsync(flockId, date, ct)) continue;

                var result = await inventory.RecordWaterUsageAsync(new RecordWaterUsageCommand(
                    flockId, date, WaterUsagePerFlockPerDay, Unit: "L", Source: "Well",
                    MeterStart: null, MeterEnd: null, Note: "Simulation fixture daily water"), accountId, ct);
                Require(result, $"record water usage for flock {flockId} on {date:yyyy-MM-dd}");
            }
    }

    private static readonly string[] ExpenseCategoriesWanted = ["Sim Utilities", "Sim Repairs & Maintenance"];

    // Deterministic recurring expense amount — see RecurringStartDay/
    // RecurringCadenceDays in the Sales partial for why this exists (same thin-tail problem
    // as sales, same fix).
    private const long RecurringExpenseAmountMinorUnits = 4_000;

    private async Task SeedExpensesAsync(
        Guid accountId, DateOnly today, IReadOnlyList<Guid> flockIds, SimCast cast, SimulationOptions sim,
        CancellationToken ct)
    {
        var categoryIds = await SeedExpenseCategoriesAsync(accountId, cast, ct);

        // flockIds[1] is safe here: RestrictOneWorkerAsync already throws if
        // fewer than 2 flocks exist, and it runs before this step.
        (string Category, int DaysAgo, string Description, long AmountMinorUnits, Guid? FlockId)[] wanted =
        [
            ("Sim Utilities", 7, "Sim Electricity Bill", 15_000, null),
            ("Sim Utilities", 3, "Sim Water Utility Bill", 8_000, null),
            ("Sim Repairs & Maintenance", 5, "Sim Coop Roof Repair", 42_000, flockIds[0]),
            ("Sim Repairs & Maintenance", 2, "Sim Feeder Replacement Part", 6_500, flockIds[1]),
        ];

        // Expense.Create is audited — a manager books the farm's spending (#500).
        // Rotates per expense so a wider Managers pool spreads across them.
        for (var i = 0; i < wanted.Length; i++)
        {
            var (category, daysAgo, description, amount, flockId) = wanted[i];
            await EnsureExpenseAsync(
                accountId, categoryIds[category], today.AddDays(-daysAgo), description, amount, flockId,
                Pick(cast.Managers, i, cast.Owner, "Managers"), ct);
        }

        await SeedRecurringExpensesAsync(accountId, today, flockIds, categoryIds, cast, sim, ct);
    }

    // Spreads a modest, deterministic drip of additional expenses across the
    // full history window — mirrors SeedRecurringOrdersAsync. Category and
    // flock attribution alternate so both categories and both flock-attributed
    // / farm-wide expenses keep growing as HistoryDays grows, not just the
    // count.
    private async Task SeedRecurringExpensesAsync(
        Guid accountId, DateOnly today, IReadOnlyList<Guid> flockIds,
        IReadOnlyDictionary<string, Guid> categoryIds, SimCast cast, SimulationOptions sim, CancellationToken ct)
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
                RecurringExpenseAmountMinorUnits, flockId,
                Pick(cast.Managers, i, cast.Owner, "Managers"), ct);
        }
    }

    private async Task<IReadOnlyDictionary<string, Guid>> SeedExpenseCategoriesAsync(
        Guid accountId, SimCast cast, CancellationToken ct)
    {
        var categoryIds = new Dictionary<string, Guid>();
        for (var i = 0; i < ExpenseCategoriesWanted.Length; i++)
            categoryIds[ExpenseCategoriesWanted[i]] = await EnsureExpenseCategoryAsync(
                accountId, ExpenseCategoriesWanted[i], Pick(cast.Managers, i, cast.Owner, "Managers"), ct);
        return categoryIds;
    }

    private async Task<Guid> EnsureExpenseCategoryAsync(
        Guid accountId, string name, SimActor actor, CancellationToken ct)
    {
        if (await financeFixture.FindCategoryIdByNameAsync(name, ct) is Guid existingId) return existingId;

        ActAs(actor);
        var result = await finance.CreateCategoryAsync(new CreateExpenseCategoryCommand(name), accountId, ct);
        Require(result, $"create expense category {name}");
        return result.Value;
    }

    private async Task EnsureExpenseAsync(
        Guid accountId, Guid categoryId, DateOnly date, string description, long amountMinorUnits, Guid? flockId,
        SimActor actor, CancellationToken ct)
    {
        // Description is the natural key THIS seeder controls (every entry
        // above is a distinct fixture string) — same convention as
        // FindOrderAsync's (CustomerId, OrderDate) in the Sales partial.
        if (await financeFixture.ExpenseExistsAsync(description, ct)) return;

        ActAs(actor);
        var result = await finance.CreateExpenseAsync(new CreateExpenseCommand(
            categoryId, date, description, amountMinorUnits, flockId,
            Note: "Simulation fixture expense"), accountId, ct);
        Require(result, $"create expense {description}");
    }
}
