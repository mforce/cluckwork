namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;

// #99 — sales lines sell products in packed units. The line snapshots the
// grade mapping and the eggs-per-unit factor at creation; allocation runs on
// quantity_base (individual eggs).
[Collection(IntegrationCollection.Name)]
public sealed class SalesProductTests(CluckworkWebApplicationFactory factory)
{
    private sealed record Created(Guid Id);
    private sealed record ItemCreated(Guid OrderId, Guid ItemId);
    private sealed record ItemDto(
        Guid Id, Guid ProductId, Guid EggGradeId, string Unit, int BaseUnitFactor,
        int Quantity, int QuantityBase, long UnitPriceMinorUnits);
    private sealed record OrderDto(Guid Id, string Status, long TotalMinorUnits, List<ItemDto> Items);
    private sealed record ConversionRow(Guid Id, string UnitCode, int EggsPerUnit, bool Active, int Version);

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

    private async Task<(HttpClient Client, Guid AccountId, Guid FarmId, Dictionary<string, Guid> Grades, Guid ProductId)>
        SetupAsync(long? defaultPrice = 100)
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var farmId = Guid.NewGuid();
        var grades = await factory.SeedEggGradesAsync(accountId, farmId, "Large", "Medium");
        var productId = await factory.SeedProductAsync(
            accountId, farmId, grades["Large"], "Large Eggs", defaultPrice);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        return (client, accountId, farmId, grades, productId);
    }

    private static async Task<Guid> CreateDraftAsync(HttpClient client)
    {
        var customer = await client.PostWithKeyAsync("/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = $"Buyer {Guid.NewGuid():N}"[..20], phone = "555-0100" });
        var customerId = (await customer.Content.ReadFromJsonAsync<Created>())!.Id;
        var order = await client.PostWithKeyAsync("/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId, orderDate = Today });
        return (await order.Content.ReadFromJsonAsync<Created>())!.Id;
    }

    private static Task<HttpResponseMessage> AddLineAsync(
        HttpClient client, Guid orderId, Guid productId, int quantity,
        string? unit = null, long? price = null) =>
        client.PostWithKeyAsync($"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { productId, quantity, unit, unitPriceMinorUnits = price });

    // Spec §9.7: the factor is snapshotted at line creation — redefining the
    // carton later must never reinterpret an existing line.
    [Fact]
    public async Task PackedUnit_FactorSnapshots_RedefineOnlyAffectsFutureLines()
    {
        var (client, _, _, _, productId) = await SetupAsync();
        var orderId = await CreateDraftAsync(client);

        // 2 cartons @ default 12 eggs → 24 eggs; price 100/carton.
        var first = await AddLineAsync(client, orderId, productId, 2, unit: "Carton");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // The market changes: a carton is now 30 eggs.
        var conversions = await client.GetFromJsonAsync<List<ConversionRow>>("/api/v1/egg-unit-conversions");
        var carton = conversions!.Single(c => c.UnitCode == "Carton");
        var put = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/egg-unit-conversions/{carton.Id}")
        { Content = JsonContent.Create(new { eggsPerUnit = 30, active = true }) };
        put.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(put)).StatusCode);

        Assert.Equal(HttpStatusCode.Created,
            (await AddLineAsync(client, orderId, productId, 1, unit: "Carton")).StatusCode);

        var order = await client.GetFromJsonAsync<OrderDto>($"/api/v1/sales/{orderId}");
        // Old line keeps 2 × 12 = 24; new line resolves 1 × 30.
        Assert.Equal(2, order!.Items.Count);
        Assert.Contains(order.Items, i => i.BaseUnitFactor == 12 && i.Quantity == 2 && i.QuantityBase == 24);
        Assert.Contains(order.Items, i => i.BaseUnitFactor == 30 && i.Quantity == 1 && i.QuantityBase == 30);
        // Total is per selling unit: (2 + 1) × 100.
        Assert.Equal(300, order.TotalMinorUnits);
    }

    // Allocation and the oversell guard run on quantity_base, not unit count.
    [Fact]
    public async Task Confirm_AllocatesEggs_NotUnits_AndGuardsOversell()
    {
        var (client, accountId, _, grades, productId) = await SetupAsync();
        await factory.SeedEggLotAsync(accountId, grades["Large"], 30);

        // 2 dozen = 24 eggs from a 30-egg lot → OK.
        var orderA = await CreateDraftAsync(client);
        Assert.Equal(HttpStatusCode.Created,
            (await AddLineAsync(client, orderA, productId, 2, unit: "Dozen")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostWithKeyAsync($"/api/v1/sales/{orderA}/confirm", Guid.NewGuid().ToString())).StatusCode);

        // 1 dozen = 12 eggs from the remaining 6 → 422, names the grade.
        var orderB = await CreateDraftAsync(client);
        Assert.Equal(HttpStatusCode.Created,
            (await AddLineAsync(client, orderB, productId, 1, unit: "Dozen")).StatusCode);
        var oversell = await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderB}/confirm", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, oversell.StatusCode);
        Assert.Contains("Large", await oversell.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AddLine_DefaultsUnitAndPriceFromProduct_GuardsMissingPieces()
    {
        var (client, accountId, farmId, grades, productId) = await SetupAsync(defaultPrice: 450);
        var orderId = await CreateDraftAsync(client);

        // No unit, no price → product defaults (unit Egg → factor 1, price 450).
        Assert.Equal(HttpStatusCode.Created,
            (await AddLineAsync(client, orderId, productId, 10)).StatusCode);
        var order = await client.GetFromJsonAsync<OrderDto>($"/api/v1/sales/{orderId}");
        var line = Assert.Single(order!.Items);
        Assert.Equal(("Egg", 1, 10, 10, 450L),
            (line.Unit, line.BaseUnitFactor, line.Quantity, line.QuantityBase, line.UnitPriceMinorUnits));

        // A product without a default price and no explicit price → 422.
        var priceless = await factory.SeedProductAsync(
            accountId, farmId, grades["Medium"], "Priceless", defaultPriceMinorUnits: null);
        var noPrice = await AddLineAsync(client, orderId, priceless, 1);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noPrice.StatusCode);
        Assert.Contains("PriceRequired", await noPrice.Content.ReadAsStringAsync());

        // Deactivated conversion → 422 naming the unit.
        var conversions = await client.GetFromJsonAsync<List<ConversionRow>>("/api/v1/egg-unit-conversions");
        var tray = conversions!.Single(c => c.UnitCode == "Tray");
        var off = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/egg-unit-conversions/{tray.Id}")
        { Content = JsonContent.Create(new { eggsPerUnit = tray.EggsPerUnit, active = false }) };
        off.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(off)).StatusCode);
        var noConv = await AddLineAsync(client, orderId, productId, 1, unit: "Tray", price: 100);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noConv.StatusCode);
        Assert.Contains("Tray", await noConv.Content.ReadAsStringAsync());

        // Inactive product → 422.
        await client.PostWithKeyAsync($"/api/v1/products/{priceless}/deactivate", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await AddLineAsync(client, orderId, priceless, 1, price: 100)).StatusCode);
    }

    // Tech spec §4.2: cross-tenant coverage for the new product→mapping→grade
    // path. A foreign product id must behave exactly like a nonexistent one.
    [Fact]
    public async Task AddLine_ForeignTenantProduct_RejectedLikeNonexistent()
    {
        var (_, _, _, _, productB) = await SetupAsync();
        var (clientA, _, _, _, _) = await SetupAsync();
        var orderId = await CreateDraftAsync(clientA);

        var foreign = await AddLineAsync(clientA, orderId, productB, 1, price: 100);
        var missing = await AddLineAsync(clientA, orderId, Guid.NewGuid(), 1, price: 100);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
        Assert.Equal(await missing.Content.ReadAsStringAsync(),
            await foreign.Content.ReadAsStringAsync());
    }

    // The edit path recomputes QuantityBase with the stored factor — it needs
    // the same overflow guard as add (a wrapped-negative base would confirm a
    // sale that consumed no stock).
    [Fact]
    public async Task UpdateLine_OverflowingEggCount_Rejected()
    {
        var (client, _, _, _, productId) = await SetupAsync();
        var orderId = await CreateDraftAsync(client);
        var add = await AddLineAsync(client, orderId, productId, 1, unit: "Case"); // factor 360
        var itemId = (await add.Content.ReadFromJsonAsync<ItemCreated>())!.ItemId;

        var put = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/sales/{orderId}/items/{itemId}")
        { Content = JsonContent.Create(new { quantity = 6_000_000, unitPriceMinorUnits = 100 }) };
        put.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await client.SendAsync(put);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("QuantityTooLarge", await response.Content.ReadAsStringAsync());

        // The line is untouched.
        var order = await client.GetFromJsonAsync<OrderDto>($"/api/v1/sales/{orderId}");
        Assert.Equal(360, Assert.Single(order!.Items).QuantityBase);
    }

    // Re-pointing a product's grade affects future lines only — old lines keep
    // the grade they were sold against.
    [Fact]
    public async Task GradeRepoint_OldLinesKeepTheirGrade()
    {
        var (client, _, _, grades, productId) = await SetupAsync();
        var orderId = await CreateDraftAsync(client);
        Assert.Equal(HttpStatusCode.Created,
            (await AddLineAsync(client, orderId, productId, 5)).StatusCode);

        var put = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/products/{productId}")
        {
            Content = JsonContent.Create(new
            {
                name = "Large Eggs", defaultUnit = "Egg",
                defaultPriceMinorUnits = (long?)100, eggGradeId = grades["Medium"],
                notes = (string?)null,
            })
        };
        put.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(put)).StatusCode);

        Assert.Equal(HttpStatusCode.Created,
            (await AddLineAsync(client, orderId, productId, 3)).StatusCode);

        var order = await client.GetFromJsonAsync<OrderDto>($"/api/v1/sales/{orderId}");
        Assert.Contains(order!.Items, i => i.Quantity == 5 && i.EggGradeId == grades["Large"]);
        Assert.Contains(order.Items, i => i.Quantity == 3 && i.EggGradeId == grades["Medium"]);
    }
}
