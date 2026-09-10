namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

// #721 — a discount reason is required at confirm, and only when a line is
// priced strictly below the list price snapshotted onto it when it was added.
[Collection(IntegrationCollection.Name)]
public sealed class SalesDiscountReasonTests(CluckworkWebApplicationFactory factory)
{
    private const long ListPrice = 45;

    private sealed record IdDto(Guid Id);
    private sealed record OrderItemDto(long UnitPriceMinorUnits, long? ListUnitPriceMinorUnits);
    private sealed record OrderDto(
        Guid Id, string Status, string? DiscountReasonCode, string? DiscountReasonNote,
        List<OrderItemDto> Items);

    // productPrice null seeds a product with no default price, which is the
    // only way AddOrderItem leaves the line's list price NULL.
    private async Task<(HttpClient Client, Guid AccountId, Guid OrderId)> DraftAsync(
        long unitPrice, long? productPrice = ListPrice)
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var farmId = Guid.NewGuid();
        var grades = await factory.SeedEggGradesAsync(accountId, farmId, "Large");
        var productId = await factory.SeedProductAsync(
            accountId, farmId, grades["Large"], "Large Eggs", productPrice);
        await factory.SeedEggLotAsync(accountId, grades["Large"], 500);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        var customerId = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(), new { name = "C", phone = "1" }));
        var orderId = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId, orderDate = DateOnly.FromDateTime(DateTime.UtcNow.Date) }));
        var addItem = await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { productId, quantity = 10, unitPriceMinorUnits = unitPrice });
        Assert.Equal(HttpStatusCode.Created, addItem.StatusCode);

        // The fixture is only meaningful if the list price actually landed on
        // the line — a silently NULL snapshot would make every "below list"
        // case below pass for the wrong reason.
        var order = await client.GetFromJsonAsync<OrderDto>($"/api/v1/sales/{orderId}");
        Assert.Equal(productPrice, order!.Items[0].ListUnitPriceMinorUnits);

        return (client, accountId, orderId);
    }

    private static async Task<Guid> CreatedId(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<IdDto>())!.Id;
    }

    private static Task<HttpResponseMessage> ConfirmAsync(
        HttpClient client, Guid orderId, object? body = null) =>
        client.PostWithKeyAsync($"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString(), body);

    private static async Task<string> TitleAsync(HttpResponseMessage response)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("title").GetString()!;
    }

    [Fact]
    public async Task Confirm_BelowList_WithNoReason_Is422()
    {
        var (client, _, orderId) = await DraftAsync(unitPrice: 1);

        var confirm = await ConfirmAsync(client, orderId);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, confirm.StatusCode);
        Assert.Equal("SalesOrder.DiscountReasonRequired", await TitleAsync(confirm));

        var order = await client.GetFromJsonAsync<OrderDto>($"/api/v1/sales/{orderId}");
        Assert.Equal("Draft", order!.Status);
    }

    [Fact]
    public async Task Confirm_BelowList_WithAReason_Succeeds_AndPersistsBothFields()
    {
        var (client, accountId, orderId) = await DraftAsync(unitPrice: 1);

        var confirm = await ConfirmAsync(client, orderId, new
        {
            discountReasonCode = "DamagedStock",
            discountReasonNote = "  hail damage  ",
        });
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);

        var order = await client.GetFromJsonAsync<OrderDto>($"/api/v1/sales/{orderId}");
        Assert.Equal("Confirmed", order!.Status);
        Assert.Equal("DamagedStock", order.DiscountReasonCode);
        Assert.Equal("hail damage", order.DiscountReasonNote);

        // Read back from the database, not only from the response the same
        // request produced: the columns are what #725 and the export will read.
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var stored = await db.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
            Assert.Equal(Domain.Sales.DiscountReasonCode.DamagedStock, stored.DiscountReasonCode);
            Assert.Equal("hail damage", stored.DiscountReasonNote);
        });
    }

    [Fact]
    public async Task Confirm_AtList_WithNoReason_Succeeds_AndLeavesBothFieldsNull()
    {
        var (client, _, orderId) = await DraftAsync(unitPrice: ListPrice);

        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(client, orderId)).StatusCode);

        var order = await client.GetFromJsonAsync<OrderDto>($"/api/v1/sales/{orderId}");
        Assert.Equal("Confirmed", order!.Status);
        Assert.Null(order.DiscountReasonCode);
        Assert.Null(order.DiscountReasonNote);
    }

    [Fact]
    public async Task Confirm_AboveList_NeedsNoReason()
    {
        var (client, _, orderId) = await DraftAsync(unitPrice: ListPrice + 1);

        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(client, orderId)).StatusCode);
    }

    [Fact]
    public async Task Confirm_LineWithNoListPrice_NeedsNoReason()
    {
        var (client, _, orderId) = await DraftAsync(unitPrice: 1, productPrice: null);

        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(client, orderId)).StatusCode);
    }

    [Fact]
    public async Task Confirm_AtList_WithAReason_Is422()
    {
        var (client, _, orderId) = await DraftAsync(unitPrice: ListPrice);

        var confirm = await ConfirmAsync(client, orderId, new { discountReasonCode = "Volume" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, confirm.StatusCode);
        Assert.Equal("SalesOrder.DiscountReasonNotApplicable", await TitleAsync(confirm));
    }

    [Fact]
    public async Task Confirm_Other_WithNoNote_Is422()
    {
        var (client, _, orderId) = await DraftAsync(unitPrice: 1);

        var confirm = await ConfirmAsync(client, orderId, new { discountReasonCode = "Other" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, confirm.StatusCode);
        Assert.Equal("SalesOrder.DiscountReasonNoteRequired", await TitleAsync(confirm));
    }

    // The validator's half: a malformed request is a 400 before any lock is
    // taken, not a 422 from the aggregate.
    [Theory]
    [InlineData("volume")]        // right member, wrong case
    [InlineData("Discount")]      // not a member
    [InlineData("0")]             // Enum.TryParse accepts a numeric string
    [InlineData("99")]            // ... including one no member names
    public async Task Confirm_WithAnUnknownReasonCode_Is400(string code)
    {
        var (client, _, orderId) = await DraftAsync(unitPrice: 1);

        var confirm = await ConfirmAsync(client, orderId, new { discountReasonCode = code });

        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
    }

    [Fact]
    public async Task Confirm_WithAnOverlongNote_Is400()
    {
        var (client, _, orderId) = await DraftAsync(unitPrice: 1);

        var confirm = await ConfirmAsync(client, orderId, new
        {
            discountReasonCode = "Other",
            discountReasonNote = new string('x', Domain.Sales.SalesOrder.MaxDiscountReasonNoteLength + 1),
        });

        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
    }

    // The Version++ rule's parallel-race test, in the shape
    // ParallelAddItems_TotalMatchesPersistedItems already uses. Two concurrent
    // confirms of one below-list order: one wins, the other is refused, and the
    // order's Version moves exactly once.
    [Fact]
    public async Task ParallelConfirms_OnlyOneWins_AndVersionMovesOnce()
    {
        var (client, accountId, orderId) = await DraftAsync(unitPrice: 1);
        var before = await VersionAsync(accountId, orderId);

        var body = new { discountReasonCode = "Volume" };
        var responses = await Task.WhenAll(
            ConfirmAsync(client, orderId, body), ConfirmAsync(client, orderId, body));

        Assert.All(responses, r => Assert.True(
            r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict,
            $"unexpected {(int)r.StatusCode}"));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);

        Assert.Equal(before + 1, await VersionAsync(accountId, orderId));
        var order = await client.GetFromJsonAsync<OrderDto>($"/api/v1/sales/{orderId}");
        Assert.Equal("Confirmed", order!.Status);
        Assert.Equal("Volume", order.DiscountReasonCode);
    }

    private async Task<int> VersionAsync(Guid accountId, Guid orderId)
    {
        var version = 0;
        await factory.WithTenantScopeAsync(accountId, async db =>
            version = (await db.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == orderId)).Version);
        return version;
    }

    // #95 — the export is the farm's own copy of its records, so the reason
    // travels with the order row. Header AND value: a header-only check passes
    // against a column wired to the wrong property.
    [Fact]
    public async Task Export_SalesOrders_CarriesTheDiscountReason()
    {
        var (client, _, orderId) = await DraftAsync(unitPrice: 1);
        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(client, orderId, new
        {
            discountReasonCode = "LongStandingCustomer",
            discountReasonNote = "standing agreement",
        })).StatusCode);

        var csv = await (await client.GetAsync("/api/v1/export/sales-orders"))
            .Content.ReadAsStringAsync();
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var header = lines[0];

        Assert.Contains("discountReasonCode", header, StringComparison.Ordinal);
        Assert.Contains("discountReasonNote", header, StringComparison.Ordinal);
        // Both cells, adjacent and in order, between the empty voidReason cell
        // and the version — so a column wired to the wrong property fails here.
        Assert.Contains(",,LongStandingCustomer,standing agreement,", csv, StringComparison.Ordinal);

        // Length guard: ExportQueries pairs the header array with the value
        // array by hand, and a column landing in one and not the other would
        // shift every cell after it silently.
        var headerFieldCount = header.Split(',').Length;
        foreach (var line in lines.Skip(1))
            Assert.Equal(headerFieldCount, line.Split(',').Length);
    }
}
