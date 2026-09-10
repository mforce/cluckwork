namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using System.Text.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Features.Sales.AddOrderItem;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #722 — the sales-line audit events carry the PRICES, so a discount given in
// the past is reconstructible from stored data. #720 put
// ListUnitPriceMinorUnits and ListPriceBasis on the line itself; this pins what
// the AUDIT PAYLOAD records, which is the half that survives a line being
// edited or removed.
[Collection(IntegrationCollection.Name)]
public sealed class SalesOrderAuditPayloadTests(CluckworkWebApplicationFactory factory)
{
    private sealed record IdDto(Guid Id);
    private sealed record AddedItemDto(Guid OrderId, Guid ItemId);
    private sealed record AuditRow(Guid Id, string Action, Guid EntityId, string? DetailsJson);

    // Every value distinct, pairwise, on purpose. Review round 1 found that a
    // fixture with the line added AT the list price made a swap of
    // before.unitPriceMinorUnits and listUnitPriceMinorUnits undetectable —
    // both read 500, so both assertions passed either way. Three prices and
    // two quantities mean any transposition of any two payload fields reddens.
    private const long ListPrice = 500;          // the product's catalogue price
    private const long AddedAtPrice = 450;       // what the line was first sold at
    private const long DiscountedPrice = 400;    // what it was then edited down to
    private const int AddedQuantity = 10;
    private const int EditedQuantity = 7;

    // The product is seeded WITH a default price on purpose: ListPriceBasis is
    // Recorded only when the product has one in the order's currency
    // (AddOrderItemHandler). Seeded unpriced, the basis would be
    // ProductUnpriced and every basis assertion below would pin the wrong
    // member while still passing.
    private async Task<(HttpClient Client, Guid AccountId, Guid ProductId)> SetupAsync()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var farmId = Guid.NewGuid();
        var grades = await factory.SeedEggGradesAsync(accountId, farmId, "Large");
        var productId = await factory.SeedProductAsync(
            accountId, farmId, grades["Large"], "Large Eggs", ListPrice);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        return (client, accountId, productId);
    }

    private static async Task<Guid> CreatedId(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<IdDto>())!.Id;
    }

    private async Task<Guid> DraftOrderAsync(HttpClient client)
    {
        var customerId = await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = "Mercado Central", phone = "555-0100" }));
        return await CreatedId(await client.PostWithKeyAsync(
            "/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId, orderDate = DateOnly.FromDateTime(DateTime.UtcNow.Date) }));
    }

    private async Task<AuditRow> SingleAuditRowAsync(HttpClient client, string action, Guid orderId)
    {
        var rows = await client.GetFromJsonAsync<List<AuditRow>>(
            $"/api/v1/audit?action={action}&entityId={orderId}");
        return Assert.Single(rows!);
    }

    [Fact]
    public async Task UpdateItem_RecordsThePriceItChangedFromAndTo()
    {
        var (client, _, productId) = await SetupAsync();
        var orderId = await DraftOrderAsync(client);

        var added = await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { productId, quantity = AddedQuantity, unitPriceMinorUnits = AddedAtPrice });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var itemId = (await added.Content.ReadFromJsonAsync<AddedItemDto>())!.ItemId;

        var edited = await client.PutWithKeyAsync(
            $"/api/v1/sales/{orderId}/items/{itemId}", Guid.NewGuid().ToString(),
            new { quantity = EditedQuantity, unitPriceMinorUnits = DiscountedPrice });
        // 204, not 200: SaleEndpoints.UpdateOrderItem returns Results.NoContent()
        // on success (SaleEndpoints.cs:156).
        Assert.Equal(HttpStatusCode.NoContent, edited.StatusCode);

        var row = await SingleAuditRowAsync(client, "SalesOrder.UpdateItem", orderId);
        using var details = JsonDocument.Parse(row.DetailsJson!);
        var root = details.RootElement;

        // The line this event is about. The Update path knows the real id
        // because the caller supplied it, and productId survives the line's
        // later removal, which cascades the row away.
        Assert.Equal(itemId, root.GetProperty("salesOrderItemId").GetGuid());
        Assert.Equal(productId, root.GetProperty("productId").GetGuid());

        // The price it changed FROM and the price it changed TO. Reading these
        // through a reference held across order.UpdateItem would make them
        // equal — SalesOrderItem.Update mutates the tracked instance in place.
        //
        // All three prices differ, so this also pins WHICH field is which: a
        // transposition of before.unitPriceMinorUnits and
        // listUnitPriceMinorUnits reddens here, where a fixture that added the
        // line at the list price could not see it (review round 1).
        var before = root.GetProperty("before").GetProperty("unitPriceMinorUnits").GetInt64();
        var after = root.GetProperty("after").GetProperty("unitPriceMinorUnits").GetInt64();
        Assert.Equal(AddedAtPrice, before);
        Assert.Equal(DiscountedPrice, after);
        Assert.NotEqual(before, after);

        // Quantity moved too, so before and after are distinguishable. Without
        // this the payload could duplicate or reverse them undetected.
        var beforeQty = root.GetProperty("before").GetProperty("quantity").GetInt32();
        var afterQty = root.GetProperty("after").GetProperty("quantity").GetInt32();
        Assert.Equal(AddedQuantity, beforeQty);
        Assert.Equal(EditedQuantity, afterQty);
        Assert.NotEqual(beforeQty, afterQty);

        // The basis by NAME, never its ordinal (Recorded is 0). GetString()
        // throws on a number, so a stored 0 fails loudly here.
        Assert.Equal("Recorded", root.GetProperty("listPriceBasis").GetString());
        Assert.Equal(ListPrice, root.GetProperty("listUnitPriceMinorUnits").GetInt64());

        // The denomination every money value above is in. Written by the
        // handler and, until review round 1, asserted on the Add path only.
        Assert.Equal("USD", root.GetProperty("currencyCode").GetString());
        Assert.Equal(2, root.GetProperty("currencyMinorUnit").GetInt32());
    }

    [Fact]
    public async Task AddItem_RecordsTheLineIdItCreated()
    {
        var (client, _, productId) = await SetupAsync();
        var orderId = await DraftOrderAsync(client);

        var added = await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { productId, quantity = 10, unitPriceMinorUnits = DiscountedPrice });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var itemId = (await added.Content.ReadFromJsonAsync<AddedItemDto>())!.ItemId;

        // Control: the endpoint's own id is real, so a Guid.Empty below is the
        // payload's fault and not the fixture's.
        Assert.NotEqual(Guid.Empty, itemId);

        var row = await SingleAuditRowAsync(client, "SalesOrder.AddItem", orderId);
        using var details = JsonDocument.Parse(row.DetailsJson!);

        // EF assigns SalesOrderItem.Id during SaveChanges. Serialising the
        // payload before that save stores Guid.Empty, silently and forever —
        // which is why the audit write follows an interleaved save inside one
        // transaction. Pinned here; mutation row M3 is the proof.
        var recorded = details.RootElement.GetProperty("salesOrderItemId").GetGuid();
        Assert.NotEqual(Guid.Empty, recorded);
        Assert.Equal(itemId, recorded);
    }

    [Fact]
    public async Task AddItem_RecordsListPriceBasisByName()
    {
        var (client, _, productId) = await SetupAsync();
        var orderId = await DraftOrderAsync(client);

        var added = await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(),
            new { productId, quantity = 10, unitPriceMinorUnits = DiscountedPrice });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);

        var row = await SingleAuditRowAsync(client, "SalesOrder.AddItem", orderId);
        using var details = JsonDocument.Parse(row.DetailsJson!);
        var root = details.RootElement;

        // The NAME, never the ordinal. AuditWriter's JsonSerializerOptions
        // register no enum converter, so a bare enum stores as 0 — and a stored
        // 0 re-reads as a different member the moment anyone reorders
        // ListPriceBasis. GetString() throws on a number, so this fails loudly.
        Assert.Equal("Recorded", root.GetProperty("listPriceBasis").GetString());

        // Sold below list: this pair is the discount, recorded at the moment it
        // was given rather than derived later against a catalogue that moves.
        Assert.Equal(ListPrice, root.GetProperty("listUnitPriceMinorUnits").GetInt64());
        Assert.Equal(DiscountedPrice, root.GetProperty("unitPriceMinorUnits").GetInt64());
        Assert.Equal(productId, root.GetProperty("productId").GetGuid());
        Assert.Equal(10, root.GetProperty("quantity").GetInt32());
        Assert.Equal("USD", root.GetProperty("currencyCode").GetString());
        Assert.Equal(2, root.GetProperty("currencyMinorUnit").GetInt32());
    }

    [Fact]
    public async Task AddItem_WhenTheAuditWriteFails_RollsBackTheLine()
    {
        var (client, accountId, productId) = await SetupAsync();
        var orderId = await DraftOrderAsync(client);

        // The OWNED transaction path: a direct handler call, so there is no
        // ambient transaction from IdempotencyMiddleware for the unit of work
        // to join — the shape both seeders run in. Tenant resolved, actor
        // deliberately NOT: AuditWriter then throws at its own actor guard
        // (#500), which is a fault BETWEEN the save that assigns the line id
        // and the commit. Driving this over HTTP would prove nothing — the
        // middleware's transaction rolls every design back identically.
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(accountId);
            var handler = scope.ServiceProvider.GetRequiredService<AddOrderItemHandler>();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                handler.HandleAsync(
                    new AddOrderItemCommand(orderId, productId, 10, null, DiscountedPrice),
                    accountId,
                    CancellationToken.None));
        }

        // The sale must not have survived the fault.
        var lines = await factory.WithTenantScopeAsync(accountId, db =>
            db.SalesOrderItems.AsNoTracking()
                .Where(i => i.SalesOrderId == orderId)
                .CountAsync());
        Assert.Equal(0, lines);
    }
}
