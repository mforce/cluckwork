using Cluckwork.Application.Modules.Commerce.Contracts;

namespace Cluckwork.Infrastructure.Persistence;

public sealed partial class SimulationDataSeeder
{
    // --- Sales: catalog, customers, orders across the lifecycle, FIFO
    // depletion, payments (#243 Task 3b) ------------------------------------
    //
    // Everything below runs through the SAME real handlers DemoDataSeeder
    // uses (CreateProduct/CreateCustomer/CreateSalesOrder/AddOrderItem/
    // ConfirmSale/RecordPayment) — orders confirmed here allocate FIFO
    // against the graded lots the production history in the ProductionHistory partial just generated,
    // exactly as a user clicking through the Sales screen would. Idempotency
    // is per-entity existence checks (product/customer by Name, order by the
    // (CustomerId, OrderDate) natural key this seeder controls), same style
    // as EnsureUserAsync/EnsureFlockAsync in the Cast and Flocks partials.

    // One product per saleable grade, sold in individual eggs (factor 1) —
    // keeps order-item quantities directly comparable to lot sizes, which is
    // what makes the FIFO-contention shape below easy to reason about.
    //
    // #396: Cracked/Dirty are here for a different reason than the graded
    // three. Submit now mints a lot for each condition counter, so without a
    // product mapped to those grades the fixture accumulates stock that can
    // never leave — ~180 lots of dead inventory the sales screens can see and
    // nothing can sell. Priced below Small, which is the point of the feature
    // (a cracked egg is saleable, at a discount). Nothing here orders them;
    // the FIFO-contention shape above stays a Large/Medium story.
    private static readonly (string GradeName, string ProductName, long PriceMinorUnits)[] CatalogWanted =
    [
        ("Large", "Sim Large Eggs", 45),
        ("Medium", "Sim Medium Eggs", 38),
        ("Small", "Sim Small Eggs", 30),
        ("Cracked", "Sim Cracked Eggs", 18),
        ("Dirty", "Sim Dirty Eggs", 22),
    ];

    // #720 — no default price at all, distinct from a below/above-list line:
    // this is the "no comparable list price" state neither of those shows.
    // Kept out of CatalogWanted/products (that dictionary is keyed by GRADE
    // NAME): mapping it to "Large" like Sim Large Eggs would silently
    // overwrite that key. No uniqueness constraint ties a grade to one
    // product (ProductEggGradeMappingConfiguration only unique-indexes
    // ProductId), so sharing the grade is fine.
    private const string UnpricedProductName = "Sim Unpriced Eggs";

    private static readonly (string Name, string Phone, string Note)[] CustomersWanted =
    [
        ("Sim Customer 1", "555-0201", "Simulation fixture customer"),
        ("Sim Customer 2", "555-0202", "Simulation fixture customer"),
        ("Sim Customer 3", "555-0203", "Simulation fixture customer"),
    ];

    // #627 — over-cap customer band: the three lifecycle rows above stay the
    // sales-order customers; 97 zero-padded fillers plus the lexically-last
    // page-two sentinel bring the account total to exactly 101, so the
    // customer list's 100-row page cannot reach the sentinel without Load more.
    private const int CustomerFillerCount = 97;
    private const string CustomerPageTwoSentinelName = "Sim Customer Z Page Two";

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

    private async Task SeedSalesAsync(
        Guid accountId, DateOnly today, SimCast cast, SimulationOptions sim, CancellationToken ct)
    {
        var grades = await LoadSaleableGradesAsync(ct);
        // The catalog is a manager's to define; the orders against it are the
        // sales desk's (#500). Product.Create and SalesOrder.* are all audited.
        var products = await SeedProductCatalogAsync(
            accountId, grades, Pick(cast.Managers, 0, cast.Owner, "Managers"), ct);
        var unpricedProductId = await EnsureProductAsync(
            accountId, UnpricedProductName, grades["Large"], null,
            Pick(cast.Managers, 0, cast.Owner, "Managers"), ct);
        var customerIds = await SeedCustomersAsync(accountId, cast, ct);

        var largeProductId = products["Large"];
        var mediumProductId = products["Medium"];
        var smallProductId = products["Small"];
        var customer1 = customerIds[0];
        var customer2 = customerIds[1];
        var customer3 = customerIds[2];

        // Each order carries its own index into the Sales pool: these five call
        // sites have no loop of their own, so the CALLER owns the rotation
        // rather than a helper inventing one.
        SimActor Clerk(int index) => Pick(cast.Sales, index, cast.Owner, "Sales");

        // Draft: created, items added, never confirmed — no stock touched.
        var draft1Id = await EnsureDraftOrderAsync(
            accountId, customer1, today.AddDays(-4), mediumProductId, DraftOrderQuantityEggs, Clerk(0), ct);
        var draft2Id = await EnsureDraftOrderAsync(
            accountId, customer2, today.AddDays(-3), mediumProductId, DraftOrderQuantityEggs, Clerk(1), ct);

        // #720 — every line seeded above sells at the product's own default
        // (both add-item call sites pass null/null), so three of the four
        // render states (below list, above list, no list price) were never
        // seeded. These land on the two EXISTING drafts, not new orders, so
        // SimulationDataSeeder's own order/line count manifest (which tracks
        // orders, never lines or products) stays untouched.
        //
        // #721 — the BELOW-list line must stay on a draft. Confirming an order
        // that carries one now requires a discount reason, and the seeders build
        // ConfirmSaleCommand directly with none, so moving this onto an order
        // EnsureConfirmedOrderAsync touches would fail the seed at runtime in a
        // verb nothing in CI runs.
        await EnsureExtraLineAsync(
            accountId, draft1Id, largeProductId, DraftOrderQuantityEggs, 35, Clerk(0), ct); // list 45 → below
        await EnsureExtraLineAsync(
            accountId, draft2Id, smallProductId, DraftOrderQuantityEggs, 40, Clerk(1), ct); // list 30 → above
        await EnsureExtraLineAsync(
            accountId, draft2Id, unpricedProductId, DraftOrderQuantityEggs, 25, Clerk(1), ct); // no list price

        // Confirmed (unpaid): same product/grade as every other confirmed
        // order below — see ConfirmedOrderQuantityEggs for why that matters.
        await EnsureConfirmedOrderAsync(
            accountId, customer1, today.AddDays(-6), largeProductId, ConfirmedOrderQuantityEggs, Clerk(2), ct);
        await EnsureConfirmedOrderAsync(
            accountId, customer2, today.AddDays(-5), largeProductId, ConfirmedOrderQuantityEggs, Clerk(3), ct);

        // Confirmed + partially paid — also left un-voided, so it doubles as
        // the "voidable confirmed order" a later hazard pass can void.
        var partialOrderId = await EnsureConfirmedOrderAsync(
            accountId, customer3, today.AddDays(-2), largeProductId, ConfirmedOrderQuantityEggs, Clerk(4), ct);
        await EnsurePartialPaymentAsync(accountId, partialOrderId, today.AddDays(-1), Clerk(4), ct);

        await SeedRecurringOrdersAsync(accountId, today, customerIds, largeProductId, cast, sim, ct);
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
        SimCast cast, SimulationOptions sim, CancellationToken ct)
    {
        var historyDays = EffectiveHistoryDays(sim);
        var i = 0;
        for (var d = RecurringStartDay; d <= historyDays; d += RecurringCadenceDays, i++)
        {
            var customerId = customerIds[i % customerIds.Count];
            await EnsureConfirmedOrderAsync(
                accountId, customerId, today.AddDays(-d), largeProductId, RecurringOrderQuantityEggs,
                Pick(cast.Sales, i, cast.Owner, "Sales"), ct);
        }
    }

    private async Task<IReadOnlyDictionary<string, Guid>> SeedProductCatalogAsync(
        Guid accountId, IReadOnlyDictionary<string, Guid> grades, SimActor actor, CancellationToken ct)
    {
        var products = new Dictionary<string, Guid>();
        foreach (var (gradeName, productName, price) in CatalogWanted)
        {
            if (!grades.TryGetValue(gradeName, out var gradeId))
                throw new InvalidOperationException(
                    $"Simulation seed needs the '{gradeName}' egg grade for the sales catalog.");
            products[gradeName] = await EnsureProductAsync(accountId, productName, gradeId, price, actor, ct);
        }
        return products;
    }

    private async Task<Guid> EnsureProductAsync(
        Guid accountId, string name, Guid eggGradeId, long? priceMinorUnits, SimActor actor, CancellationToken ct)
    {
        if (await commerceFixture.FindProductIdByNameAsync(name, ct) is Guid existingId) return existingId;

        ActAs(actor);
        var result = await commerce.CreateProductAsync(new CreateProductCommand(
            name, "Egg", "Egg", priceMinorUnits, eggGradeId, "Simulation fixture product"), accountId, ct);
        Require(result, $"create product {name}");
        return result.Value;
    }

    // Customer.Create is audited as the Sales actor declared per customer here,
    // for consistency with the orders these customers receive.
    private async Task<IReadOnlyList<Guid>> SeedCustomersAsync(
        Guid accountId, SimCast cast, CancellationToken ct)
    {
        var ids = new List<Guid>();
        for (var i = 0; i < CustomersWanted.Length; i++)
        {
            var (name, phone, note) = CustomersWanted[i];
            ids.Add(await EnsureCustomerAsync(
                accountId, name, phone, note, Pick(cast.Sales, i, cast.Owner, "Sales"), ct));
        }

        // #627 — the over-cap fillers and the lexically-last page-two sentinel.
        // The three lifecycle customers above are the only ones orders touch;
        // these rows are list volume, created through the same handler with
        // the same name-probe idempotency.
        for (var i = 1; i <= CustomerFillerCount; i++)
            await EnsureCustomerAsync(
                accountId,
                $"Sim Customer Filler {i:000}",
                $"555-03{i:00}",
                "Simulation fixture customer",
                Pick(cast.Sales, i, cast.Owner, "Sales"),
                ct);

        await EnsureCustomerAsync(
            accountId,
            CustomerPageTwoSentinelName,
            "555-0299",
            "Simulation fixture customer",
            Pick(cast.Sales, CustomerFillerCount + 1, cast.Owner, "Sales"),
            ct);

        return ids;
    }

    private async Task<Guid> EnsureCustomerAsync(
        Guid accountId, string name, string phone, string note, SimActor actor, CancellationToken ct)
    {
        if (await commerceFixture.FindCustomerIdByNameAsync(name, ct) is Guid existingId) return existingId;

        ActAs(actor);
        var result = await commerce.CreateCustomerAsync(
            new CreateCustomerCommand(name, phone, Note: note), accountId, ct);
        Require(result, $"create customer {name}");
        return result.Value;
    }

    // (CustomerId, OrderDate) is the natural key THIS seeder controls (every
    // call site below uses a distinct date per customer) — SalesOrder itself
    // has no other stable, human-chosen identity to check idempotency against
    // (ReferenceNumber is minted from a random order id inside the handler).
    private Task<Guid?> FindOrderAsync(Guid customerId, DateOnly orderDate, CancellationToken ct) =>
        commerceFixture.FindOrderIdAsync(customerId, orderDate, ct);

    private async Task<Guid> EnsureDraftOrderAsync(
        Guid accountId, Guid customerId, DateOnly orderDate, Guid productId, int quantityEggs,
        SimActor actor, CancellationToken ct)
    {
        var existing = await FindOrderAsync(customerId, orderDate, ct);
        if (existing is not null) return existing.Value;

        ActAs(actor);
        var created = await commerce.CreateSalesOrderAsync(
            new CreateSalesOrderCommand(customerId, orderDate), accountId, ct);
        Require(created, $"create sales order for customer {customerId} on {orderDate:yyyy-MM-dd}");
        var orderId = created.Value;

        // Unit/price both null: defaults from the product (Egg unit, the
        // catalog price seeded above) — same pattern as DemoDataSeeder.
        var added = await commerce.AddOrderItemAsync(
            new AddOrderItemCommand(orderId, productId, quantityEggs, null, null), accountId, ct);
        Require(added, $"add item to sales order {orderId}");

        return orderId;
    }

    // #720 — a SECOND line on an already-seeded draft, at an EXPLICIT price so
    // the fixture can show a discounted, an above-list, and an unpriced-product
    // line (every seeded line was at-list before this, since both add-item call
    // sites passed null/null). Idempotent on (SalesOrderId, ProductId): this
    // order's base line already occupies its own product, so a distinct
    // product per extra line is what keeps a rerun from re-adding it.
    private async Task EnsureExtraLineAsync(
        Guid accountId, Guid orderId, Guid productId, int quantityEggs, long explicitPriceMinorUnits,
        SimActor actor, CancellationToken ct)
    {
        if (await commerceFixture.OrderHasLineAsync(orderId, productId, ct)) return;

        ActAs(actor);
        var added = await commerce.AddOrderItemAsync(
            new AddOrderItemCommand(orderId, productId, quantityEggs, null, explicitPriceMinorUnits),
            accountId, ct);
        Require(added, $"add extra line to sales order {orderId}");
    }

    private async Task<Guid> EnsureConfirmedOrderAsync(
        Guid accountId, Guid customerId, DateOnly orderDate, Guid productId, int quantityEggs,
        SimActor actor, CancellationToken ct)
    {
        var orderId = await EnsureDraftOrderAsync(
            accountId, customerId, orderDate, productId, quantityEggs, actor, ct);

        // Status, not "the order already existed", decides whether to
        // confirm — an order that exists but is still Draft still needs
        // confirming on a re-run.
        if (await commerceFixture.IsOrderConfirmedAsync(orderId, ct)) return orderId;

        // Re-declared rather than inherited from EnsureDraftOrderAsync: on the
        // re-run path above, that call returns early without acting at all, so
        // the resolved actor would be whoever the previous phase left behind —
        // a manager, from the inventory phase — and the confirmation would be
        // authorized and audited as them.
        //
        // NOT COVERED BY ANY TEST, stated rather than left to be assumed (#500
        // mid-point review; confirmed by mutation — deleting this line leaves
        // the whole suite green). Reaching it needs a fixture where a DRAFT
        // order survives into a re-run, and the only way to build one is to
        // force a confirmed order back to Draft, which replays FIFO allocation
        // against the same lots. The line is correct and cheap; the fixture
        // that would prove it is neither.
        ActAs(actor);
        // FIFO allocation (tech spec §10.9.1): draws from the oldest
        // available lots for this grade under a FOR UPDATE lock — this is
        // what depletes the production history's egg lots.
        var confirmed = await commerce.ConfirmSaleAsync(new ConfirmSaleCommand(orderId), accountId, actor.UserId, ct);
        Require(confirmed, $"confirm sales order {orderId}");
        return orderId;
    }

    // Payment.* is audited by nothing — same standing as customers above.
    private async Task EnsurePartialPaymentAsync(
        Guid accountId, Guid orderId, DateOnly paymentDate, SimActor actor, CancellationToken ct)
    {
        if (await commerceFixture.PaymentExistsAsync(orderId, ct)) return;

        // Half the total, rounded down — strictly less than TotalAmount so
        // the order stays genuinely partially paid, never fully settled.
        var amount = await commerceFixture.GetOrderTotalMinorUnitsAsync(orderId, ct) / 2;
        if (amount <= 0)
            throw new InvalidOperationException(
                $"Simulation seed: sales order {orderId}'s total is too small to seed a partial payment.");

        ActAs(actor);
        var result = await commerce.RecordPaymentAsync(new RecordPaymentCommand(
            orderId, paymentDate, amount, "Cash", null, "Simulation fixture partial payment"), accountId, ct);
        Require(result, $"record partial payment for sales order {orderId}");
    }
}
