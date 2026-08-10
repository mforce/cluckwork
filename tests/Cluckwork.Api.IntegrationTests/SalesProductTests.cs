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
        string? unit = null, long? price = null, int? expectedFactor = null) =>
        client.PostWithKeyAsync($"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { productId, quantity, unit, unitPriceMinorUnits = price, expectedEggsPerUnit = expectedFactor });

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

    // #445 — the SPA previews "= N eggs" from a conversions read done at page
    // load and passes that factor with the write; a redefinition in between
    // must refuse rather than silently snapshot a QuantityBase different from
    // the previewed one. Probed from both sides: a matching factor sails
    // through (the guard is not overzealous), a stale one 422s, and re-adding
    // with the current factor succeeds (the refusal is recoverable).
    [Fact]
    public async Task AddLine_StaleExpectedFactor_RefusedAfterUnitRedefinition()
    {
        var (client, _, _, _, productId) = await SetupAsync();
        var orderId = await CreateDraftAsync(client);

        // Matching the current definition (Carton default 12) → accepted.
        Assert.Equal(HttpStatusCode.Created,
            (await AddLineAsync(client, orderId, productId, 1, unit: "Carton", expectedFactor: 12)).StatusCode);

        // An admin redefines the carton while the seller's page still says 12.
        var conversions = await client.GetFromJsonAsync<List<ConversionRow>>("/api/v1/egg-unit-conversions");
        var carton = conversions!.Single(c => c.UnitCode == "Carton");
        var put = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/egg-unit-conversions/{carton.Id}")
        { Content = JsonContent.Create(new { eggsPerUnit = 30, active = true }) };
        put.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(put)).StatusCode);

        var stale = await AddLineAsync(client, orderId, productId, 2, unit: "Carton", expectedFactor: 12);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, stale.StatusCode);
        Assert.Contains("UnitDefinitionChanged", await stale.Content.ReadAsStringAsync());

        // Nothing was recorded for the refused line, and retrying with the
        // current factor works.
        var order = await client.GetFromJsonAsync<OrderDto>($"/api/v1/sales/{orderId}");
        Assert.Single(order!.Items);
        Assert.Equal(HttpStatusCode.Created,
            (await AddLineAsync(client, orderId, productId, 2, unit: "Carton", expectedFactor: 30)).StatusCode);

        // A non-positive expected factor can only be a caller bug (real
        // factors are floored at 1) — rejected by validation, not compared.
        var zero = await AddLineAsync(client, orderId, productId, 1, unit: "Carton", expectedFactor: 0);
        Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);
        Assert.Contains("ExpectedEggsPerUnit", await zero.Content.ReadAsStringAsync());
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

    // #123 backstop. A catalog default price is a raw minor-unit integer in the
    // currency the product snapshotted, and the line stamps it with the ORDER's
    // currency — so if those ever diverge, $12.34 (1234) sells as ¥1,234. The
    // farm-settings currency lock is what keeps them equal; this proves the
    // sale refuses rather than mis-prices if anything gets past it.
    [Fact]
    public async Task ProductPricedInAnotherCurrency_IsRefused_NotSilentlyRelabelled()
    {
        var (client, accountId, farmId, grades, _) = await SetupAsync();
        var orderId = await CreateDraftAsync(client);

        // A product priced in JPY on a USD farm — the state a currency change
        // would leave behind if the lock ever failed.
        var foreign = Guid.NewGuid();
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.Products.Add(Cluckwork.Domain.Catalog.Product.Create(
                foreign, accountId, farmId, "Yen-priced dozen",
                Cluckwork.Domain.Catalog.ProductType.Egg,
                Cluckwork.Domain.Catalog.ProductUnit.Egg,
                defaultPriceMinorUnits: 12_34, "JPY", 0, notes: null));
            db.ProductEggGradeMappings.Add(Cluckwork.Domain.Catalog.ProductEggGradeMapping.Create(
                Guid.NewGuid(), accountId, foreign, grades["Large"]));
            await db.SaveChangesAsync();
        });

        var defaulted = await AddLineAsync(client, orderId, foreign, 1);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, defaulted.StatusCode);
        Assert.Contains("ProductPriceCurrencyMismatch", await defaulted.Content.ReadAsStringAsync());

        // An explicit price is the caller's own number in the order's currency,
        // so it is unaffected — the guard is about the DEFAULT, not the product.
        Assert.Equal(HttpStatusCode.Created,
            (await AddLineAsync(client, orderId, foreign, 1, price: 500)).StatusCode);
    }

    // The trap the guard above would otherwise spring (codex review of #159).
    // An UNPRICED product does not lock the farm currency, so this sequence is
    // entirely legal: create it unpriced, change the farm currency, then give
    // the product its first price. If that price kept the product's
    // creation-time currency, every order would refuse the default and no API
    // call could fix it. The first price binds to the currency the farm uses
    // now.
    [Fact]
    public async Task FirstPriceOnAnUnpricedProduct_BindsToTheFarmsCurrentCurrency()
    {
        // Nothing priced anywhere on this farm — otherwise the currency lock
        // fires first and the sequence under test is unreachable.
        var (client, accountId, farmId, grades, _) = await SetupAsync(defaultPrice: null);
        var productId = await factory.SeedProductAsync(
            accountId, farmId, grades["Large"], "Unpriced dozen", defaultPriceMinorUnits: null);

        // The farm leaves USD — allowed, nothing has recorded an amount yet.
        var settings = await client.GetFromJsonAsync<SettingsView>("/api/v1/account/settings");
        Assert.True(settings!.CanChangeCurrency);
        var change = new HttpRequestMessage(HttpMethod.Put, "/api/v1/account/settings")
        {
            Content = JsonContent.Create(new
            {
                settings.Settings.Name,
                settings.Settings.TimeZoneId,
                settings.Settings.Locale,
                currencyCode = "JPY",
                settings.Settings.UnitSystem,
                firstDayOfWeek = (string?)null,
                dateFormatOverride = (string?)null,
                timeFormatOverride = (string?)null,
                brand = "aubergine",
                defaultStepperUnit = "Individual",
                settings.Settings.Version
            })
        };
        change.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(change)).StatusCode);

        // Now price it for the first time.
        var put = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/products/{productId}")
        {
            Content = JsonContent.Create(new
            {
                name = "Unpriced dozen", defaultUnit = "Egg",
                defaultPriceMinorUnits = (long?)500, eggGradeId = grades["Large"],
                notes = (string?)null,
            })
        };
        put.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(put)).StatusCode);

        // A JPY order takes that default without complaint.
        var orderId = await CreateDraftAsync(client);
        Assert.Equal(HttpStatusCode.Created,
            (await AddLineAsync(client, orderId, productId, 1)).StatusCode);
    }

    private sealed record SettingsView(AccountView Settings, bool CanChangeCurrency);
    private sealed record AccountView(
        Guid Id, string Name, string CurrencyCode, int CurrencyMinorUnit, string CurrencySymbol,
        string TimeZoneId, string Locale, string UnitSystem, string? FirstDayOfWeek,
        string? DateFormatOverride, string? TimeFormatOverride, int Version);
}
