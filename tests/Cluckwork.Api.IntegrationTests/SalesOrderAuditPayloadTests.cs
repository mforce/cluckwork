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

    private const long ListPrice = 500;
    private const long DiscountedPrice = 400;

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
            new { productId, quantity = 10, unitPriceMinorUnits = ListPrice });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var itemId = (await added.Content.ReadFromJsonAsync<AddedItemDto>())!.ItemId;

        var edited = await client.PutWithKeyAsync(
            $"/api/v1/sales/{orderId}/items/{itemId}", Guid.NewGuid().ToString(),
            new { quantity = 10, unitPriceMinorUnits = DiscountedPrice });
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
        var before = root.GetProperty("before").GetProperty("unitPriceMinorUnits").GetInt64();
        var after = root.GetProperty("after").GetProperty("unitPriceMinorUnits").GetInt64();
        Assert.Equal(ListPrice, before);
        Assert.Equal(DiscountedPrice, after);
        Assert.NotEqual(before, after);

        // The basis by NAME, never its ordinal (Recorded is 0). GetString()
        // throws on a number, so a stored 0 fails loudly here.
        Assert.Equal("Recorded", root.GetProperty("listPriceBasis").GetString());
        Assert.Equal(ListPrice, root.GetProperty("listUnitPriceMinorUnits").GetInt64());
    }
}
