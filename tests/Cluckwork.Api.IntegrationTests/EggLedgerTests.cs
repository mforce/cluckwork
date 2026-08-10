namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;

// #101 — the egg movement ledger. Every lot mutation writes an explicit
// signed row in the same transaction, and the cached QuantityAvailable must
// always equal the sum of the lot's movements (tech-spec §212 rule).
[Collection(IntegrationCollection.Name)]
public sealed class EggLedgerTests(CluckworkWebApplicationFactory factory)
{
    private sealed record Created(Guid Id);
    private sealed record EntryDto(Guid Id, int Version);
    private sealed record SubmitDto(Guid Id, string Status, List<Guid> EggLotIds);
    private sealed record LotRow(
        Guid Id, Guid EggGradeId, DateOnly ProductionDate, int QuantityProduced,
        int QuantityAvailable, DateOnly? RestrictedUntil, Guid? DailyEntryId);
    private sealed record MovementRow(
        Guid Id, string MovementType, int QuantityDelta,
        string ReferenceType, Guid ReferenceId, string? Reason, DateTimeOffset CreatedAtUtc);

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

    private async Task<(HttpClient Client, Guid AccountId, Guid FarmId, Guid FlockId, Guid GradeId, Guid ProductId)>
        SetupAsync()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var farmId = Guid.NewGuid();
        var grades = await factory.SeedEggGradesAsync(accountId, farmId, "Large");
        var productId = await factory.SeedProductAsync(accountId, farmId, grades["Large"], "Large Eggs", 100);
        var flockId = await factory.SeedFlockAsync(accountId, farmId);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        return (client, accountId, farmId, flockId, grades["Large"], productId);
    }

    private static async Task<(Guid EntryId, Guid LotId, int Version)> SubmitEntryAsync(
        HttpClient client, Guid farmId, Guid flockId, Guid gradeId, int quantity,
        DateOnly? date = null)
    {
        var record = await client.PostWithKeyAsync("/api/v1/daily-entries", Guid.NewGuid().ToString(), new
        {
            farmId, houseId = Guid.NewGuid(), flockId, date = date ?? Today,
            totalEggs = quantity, crackedEggs = 0, dirtyEggs = 0, discardedEggs = 0,
            mortalityCount = 0,
            grades = new[] { new { eggGradeId = gradeId, quantity } }
        });
        var entryId = (await record.Content.ReadFromJsonAsync<Created>())!.Id;
        var submit = await client.PostWithKeyAsync(
            $"/api/v1/daily-entries/{entryId}/submit", Guid.NewGuid().ToString());
        var lotId = (await submit.Content.ReadFromJsonAsync<SubmitDto>())!.EggLotIds.Single();
        var version = (await client.GetFromJsonAsync<EntryDto>($"/api/v1/daily-entries/{entryId}"))!.Version;
        return (entryId, lotId, version);
    }

    private static Task<List<MovementRow>?> MovementsAsync(HttpClient client, Guid lotId) =>
        client.GetFromJsonAsync<List<MovementRow>>($"/api/v1/stock/lots/{lotId}/movements");

    private static async Task<LotRow> LotAsync(HttpClient client, Guid lotId, Guid gradeId)
    {
        var lots = await client.GetFromJsonAsync<List<LotRow>>($"/api/v1/stock/lots?gradeId={gradeId}");
        return lots!.Single(l => l.Id == lotId);
    }

    // The §212 invariant across the full lifecycle: submit → confirm → void
    // sale → adjust → void entry. After every step the cached balance equals
    // the sum of the ledger.
    [Fact]
    public async Task Ledger_RebuildsTheCachedBalance_AcrossTheFullLifecycle()
    {
        var (client, _, farmId, flockId, gradeId, productId) = await SetupAsync();
        var (entryId, lotId, _) = await SubmitEntryAsync(client, farmId, flockId, gradeId, 100);

        async Task AssertInvariantAsync(int expectedAvailable)
        {
            var lot = await LotAsync(client, lotId, gradeId);
            var movements = await MovementsAsync(client, lotId);
            Assert.Equal(expectedAvailable, lot.QuantityAvailable);
            Assert.Equal(lot.QuantityAvailable, movements!.Sum(m => m.QuantityDelta));
        }

        // 1. Production movement (+100).
        var production = Assert.Single((await MovementsAsync(client, lotId))!);
        Assert.Equal(("Production", 100, "DailyEntry", entryId),
            (production.MovementType, production.QuantityDelta, production.ReferenceType, production.ReferenceId));
        await AssertInvariantAsync(100);

        // 2. Sale of 2 dozen (24 eggs) → Sale movement (−24).
        var customer = await client.PostWithKeyAsync("/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = "Ledger Buyer", phone = "555-0100" });
        var customerId = (await customer.Content.ReadFromJsonAsync<Created>())!.Id;
        var order = await client.PostWithKeyAsync("/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId, orderDate = Today });
        var orderId = (await order.Content.ReadFromJsonAsync<Created>())!.Id;
        await client.PostWithKeyAsync($"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { productId, quantity = 2, unit = "Dozen" });
        Assert.Equal(HttpStatusCode.OK, (await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString())).StatusCode);

        var afterSale = await MovementsAsync(client, lotId);
        var sale = Assert.Single(afterSale!, m => m.MovementType == "Sale");
        // References the ALLOCATION (movement → allocation → item → order,
        // spec §9.6), not the order directly.
        Assert.Equal((-24, "SalesOrderAllocation"), (sale.QuantityDelta, sale.ReferenceType));
        Assert.NotEqual(orderId, sale.ReferenceId);
        await AssertInvariantAsync(76);

        // 3. Void the sale → Void movement (+24) with the reason.
        await client.PostWithKeyAsync($"/api/v1/sales/{orderId}/void", Guid.NewGuid().ToString(),
            new { reason = "wrong buyer" });
        var afterVoid = await MovementsAsync(client, lotId);
        var saleVoid = Assert.Single(afterVoid!, m => m.MovementType == "Void");
        Assert.Equal((24, "wrong buyer"), (saleVoid.QuantityDelta, saleVoid.Reason));
        await AssertInvariantAsync(100);

        // 4. Adjust the entry down to 80 → Adjustment (−20).
        var version = (await client.GetFromJsonAsync<EntryDto>($"/api/v1/daily-entries/{entryId}"))!.Version;
        var adjust = await client.PostWithKeyAsync(
            $"/api/v1/daily-entries/{entryId}/adjust", Guid.NewGuid().ToString(), new
            {
                version, totalEggs = 80, crackedEggs = 0, dirtyEggs = 0,
                discardedEggs = 0, mortalityCount = 0, reason = "recount",
                grades = new[] { new { eggGradeId = gradeId, quantity = 80 } }
            });
        Assert.Equal(HttpStatusCode.OK, adjust.StatusCode);
        var afterAdjust = await MovementsAsync(client, lotId);
        var adjustment = Assert.Single(afterAdjust!, m => m.MovementType == "Adjustment");
        Assert.Equal((-20, "recount"), (adjustment.QuantityDelta, adjustment.Reason));
        await AssertInvariantAsync(80);

        // 5. Void the entry → Void movement (−80), balance 0, ledger sums 0.
        version = (await client.GetFromJsonAsync<EntryDto>($"/api/v1/daily-entries/{entryId}"))!.Version;
        var entryVoid = await client.PostWithKeyAsync(
            $"/api/v1/daily-entries/{entryId}/void", Guid.NewGuid().ToString(),
            new { version, reason = "day never happened" });
        Assert.Equal(HttpStatusCode.OK, entryVoid.StatusCode);
        var final = await MovementsAsync(client, lotId);
        Assert.Equal(5, final!.Count);
        Assert.Contains(final, m => m.MovementType == "Void" && m.QuantityDelta == -80);
        await AssertInvariantAsync(0);
    }

    // A sale spanning two FIFO lots writes one Sale movement PER lot; movement
    // lists come back newest first; /stock/lots pages.
    [Fact]
    public async Task MultiLotSale_WritesOneMovementPerLot_NewestFirst_LotsPage()
    {
        var (client, accountId, farmId, flockId, gradeId, productId) = await SetupAsync();
        // Lot A is a day older — FIFO (ProductionDate, Id) drains it first
        // deterministically; same-day lots would tie-break on random Guids.
        var (_, lotA, _) = await SubmitEntryAsync(client, farmId, flockId, gradeId, 20, Today.AddDays(-1));
        var flockB = await factory.SeedFlockAsync(accountId, farmId);
        var (_, lotB, _) = await SubmitEntryAsync(client, farmId, flockB, gradeId, 30);

        // 3 dozen = 36 eggs: FIFO drains lot A (20) then takes 16 from lot B.
        var customer = await client.PostWithKeyAsync("/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = "FIFO Buyer", phone = "555-0100" });
        var customerId = (await customer.Content.ReadFromJsonAsync<Created>())!.Id;
        var order = await client.PostWithKeyAsync("/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId, orderDate = Today });
        var orderId = (await order.Content.ReadFromJsonAsync<Created>())!.Id;
        await client.PostWithKeyAsync($"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { productId, quantity = 3, unit = "Dozen" });
        Assert.Equal(HttpStatusCode.OK, (await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString())).StatusCode);

        var movementsA = await MovementsAsync(client, lotA);
        var movementsB = await MovementsAsync(client, lotB);
        Assert.Equal(-20, Assert.Single(movementsA!, m => m.MovementType == "Sale").QuantityDelta);
        Assert.Equal(-16, Assert.Single(movementsB!, m => m.MovementType == "Sale").QuantityDelta);
        Assert.Equal(0, (await LotAsync(client, lotA, gradeId)).QuantityAvailable);
        Assert.Equal(0, movementsA!.Sum(m => m.QuantityDelta));
        Assert.Equal(14, movementsB!.Sum(m => m.QuantityDelta));

        // Newest first: the Sale precedes Production in each list.
        Assert.Equal(new[] { "Sale", "Production" }, movementsA!.Select(m => m.MovementType));
        Assert.True(movementsA![0].CreatedAtUtc >= movementsA[1].CreatedAtUtc);

        // Paging: two windows of one, disjoint, covering both lots.
        var page1 = await client.GetFromJsonAsync<List<LotRow>>(
            $"/api/v1/stock/lots?gradeId={gradeId}&limit=1&offset=0");
        var page2 = await client.GetFromJsonAsync<List<LotRow>>(
            $"/api/v1/stock/lots?gradeId={gradeId}&limit=1&offset=1");
        Assert.Single(page1!);
        Assert.Single(page2!);
        Assert.NotEqual(page1![0].Id, page2![0].Id);
        Assert.Equal(new[] { lotA, lotB }.OrderBy(x => x),
            page1.Concat(page2).Select(l => l.Id).OrderBy(x => x));
    }

    // Adjust UP writes a positive Adjustment; a grade line ADDED by the
    // adjustment births a lot whose opening movement is Adjustment, not
    // Production (the entry was already submitted).
    [Fact]
    public async Task AdjustUp_AndAdjustmentBornLot_WritePositiveAdjustments()
    {
        var (client, accountId, farmId, flockId, gradeId, _) = await SetupAsync();
        var mediumId = (await factory.SeedEggGradesAsync(accountId, farmId, "Medium"))["Medium"];
        var (entryId, largeLot, version) = await SubmitEntryAsync(client, farmId, flockId, gradeId, 50);

        var adjust = await client.PostWithKeyAsync(
            $"/api/v1/daily-entries/{entryId}/adjust", Guid.NewGuid().ToString(), new
            {
                version, totalEggs = 90, crackedEggs = 0, dirtyEggs = 0,
                discardedEggs = 0, mortalityCount = 0, reason = "found more",
                grades = new[]
                {
                    new { eggGradeId = gradeId, quantity = 70 },
                    new { eggGradeId = mediumId, quantity = 20 },
                }
            });
        Assert.Equal(HttpStatusCode.OK, adjust.StatusCode);

        // Existing lot: Adjustment +20 (50 → 70), invariant holds.
        var largeMovements = await MovementsAsync(client, largeLot);
        Assert.Equal(20, Assert.Single(largeMovements!, m => m.MovementType == "Adjustment").QuantityDelta);
        Assert.Equal(70, largeMovements!.Sum(m => m.QuantityDelta));

        // Adjustment-born lot: opens with Adjustment +20, no Production row.
        var mediumLots = await client.GetFromJsonAsync<List<LotRow>>(
            $"/api/v1/stock/lots?gradeId={mediumId}");
        var born = Assert.Single(mediumLots!);
        var bornMovements = await MovementsAsync(client, born.Id);
        var opening = Assert.Single(bornMovements!);
        Assert.Equal(("Adjustment", 20, "found more"),
            (opening.MovementType, opening.QuantityDelta, opening.Reason));
        Assert.Equal(born.QuantityAvailable, bornMovements!.Sum(m => m.QuantityDelta));
    }

    // A failed operation must leave no ledger rows — the movement commits and
    // rolls back with the lot change it records.
    [Fact]
    public async Task FailedOperations_WriteNoMovements()
    {
        var (client, _, farmId, flockId, gradeId, productId) = await SetupAsync();
        var (_, lotId, _) = await SubmitEntryAsync(client, farmId, flockId, gradeId, 10);

        // Overselling confirm fails mid-transaction → still only Production.
        var customer = await client.PostWithKeyAsync("/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = "Oversell Buyer", phone = "555-0100" });
        var customerId = (await customer.Content.ReadFromJsonAsync<Created>())!.Id;
        var order = await client.PostWithKeyAsync("/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId, orderDate = Today });
        var orderId = (await order.Content.ReadFromJsonAsync<Created>())!.Id;
        await client.PostWithKeyAsync($"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { productId, quantity = 2, unit = "Dozen" }); // 24 > 10
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString())).StatusCode);

        var movements = await MovementsAsync(client, lotId);
        Assert.Single(movements!);
        Assert.Equal("Production", movements![0].MovementType);
    }

    // #465 — the SPA's date filter: from/to bound the lot list by production
    // date (inclusive both ends, like the daily-entries list), composing with
    // gradeId and paging so "show more" pages within the filtered window.
    [Fact]
    public async Task DateFilter_BoundsTheLotList_InclusiveAndPageable()
    {
        var (client, _, farmId, flockId, gradeId, _) = await SetupAsync();
        var (_, oldest, _) = await SubmitEntryAsync(client, farmId, flockId, gradeId, 10, Today.AddDays(-2));
        var (_, middle, _) = await SubmitEntryAsync(client, farmId, flockId, gradeId, 20, Today.AddDays(-1));
        var (_, newest, _) = await SubmitEntryAsync(client, farmId, flockId, gradeId, 30);

        async Task<List<Guid>> IdsAsync(string query) =>
            (await client.GetFromJsonAsync<List<LotRow>>($"/api/v1/stock/lots?gradeId={gradeId}&{query}"))!
            .Select(l => l.Id).ToList();

        // from alone drops older lots; to alone drops newer ones.
        Assert.Equal([newest, middle], await IdsAsync($"from={Today.AddDays(-1):yyyy-MM-dd}"));
        Assert.Equal([oldest], await IdsAsync($"to={Today.AddDays(-2):yyyy-MM-dd}"));

        // Both bounds are inclusive: a one-day window keeps exactly that day.
        Assert.Equal([middle],
            await IdsAsync($"from={Today.AddDays(-1):yyyy-MM-dd}&to={Today.AddDays(-1):yyyy-MM-dd}"));

        // Paging happens WITHIN the window: two disjoint pages of one stitch
        // back to the filtered pair, newest first.
        var page1 = await IdsAsync($"from={Today.AddDays(-1):yyyy-MM-dd}&limit=1&offset=0");
        var page2 = await IdsAsync($"from={Today.AddDays(-1):yyyy-MM-dd}&limit=1&offset=1");
        Assert.Equal([newest], page1);
        Assert.Equal([middle], page2);

        // An empty window is an empty list, not an error.
        Assert.Empty(await IdsAsync($"from={Today.AddDays(10):yyyy-MM-dd}"));
    }

    [Fact]
    public async Task LotMovements_AreTenantIsolated_ForeignLotLooksNonexistent()
    {
        var (clientA, _, farmA, flockA, gradeA, _) = await SetupAsync();
        var (_, lotA, _) = await SubmitEntryAsync(clientA, farmA, flockA, gradeA, 50);

        var (clientB, _, _, _, _, _) = await SetupAsync();
        Assert.Equal(HttpStatusCode.NotFound,
            (await clientB.GetAsync($"/api/v1/stock/lots/{lotA}/movements")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await clientB.GetAsync($"/api/v1/stock/lots/{Guid.NewGuid()}/movements")).StatusCode);
        // A's own view still works and B's lot list is empty.
        Assert.Equal(HttpStatusCode.OK, (await clientA.GetAsync($"/api/v1/stock/lots/{lotA}/movements")).StatusCode);
        Assert.Empty((await clientB.GetFromJsonAsync<List<LotRow>>("/api/v1/stock/lots"))!);
    }
}
