namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

// #66 (PR 1) — inventory foundation: item catalog, receiving stock as lots,
// and the append-only movement ledger. Consumption (feed usage) is PR 2.
[Collection(IntegrationCollection.Name)]
public sealed class InventoryTests(CluckworkWebApplicationFactory factory)
{
    private sealed record ItemDto(
        Guid Id, string Name, string Category, string Unit,
        long? DefaultCostMinorUnits, decimal QuantityOnHand, bool Active);
    private sealed record LotDto(
        Guid Id, DateOnly ReceivedDate, decimal QuantityReceived, decimal QuantityAvailable,
        long UnitCostMinorUnits);
    private sealed record MovementDto(Guid Id, DateOnly Date, string Type, decimal QuantityDelta, string Unit);
    private sealed record Created(Guid Id);

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

    private async Task<HttpClient> ClientAsync()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(email);
        return factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
    }

    private static Task<HttpResponseMessage> PutWithKeyAsync(HttpClient client, string url, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return client.SendAsync(request);
    }

    private static async Task<Guid> CreateItemAsync(
        HttpClient client, string name, long? defaultCost = 2500, string unit = "kg")
    {
        var response = await client.PostWithKeyAsync("/api/v1/inventory/items", Guid.NewGuid().ToString(),
            new { name, category = "Feed", unit, defaultUnitCostMinorUnits = defaultCost });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Created>())!.Id;
    }

    [Fact]
    public async Task Purchase_AccumulatesStock_AndWritesLedger()
    {
        var client = await ClientAsync();
        var item = await CreateItemAsync(client, "Layer feed 17%");

        var first = await client.PostWithKeyAsync(
            $"/api/v1/inventory/items/{item}/purchases", Guid.NewGuid().ToString(),
            new { receivedDate = Today.AddDays(-3), quantity = 500m, unitCostMinorUnits = 2400, lotNumber = "A-100" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // No explicit cost: falls back to the item's default (2500).
        var second = await client.PostWithKeyAsync(
            $"/api/v1/inventory/items/{item}/purchases", Guid.NewGuid().ToString(),
            new { receivedDate = Today, quantity = 250.5m, unitCostMinorUnits = (long?)null });
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        var fetched = await client.GetFromJsonAsync<ItemDto>($"/api/v1/inventory/items/{item}");
        Assert.Equal(750.5m, fetched!.QuantityOnHand);

        var lots = await client.GetFromJsonAsync<List<LotDto>>($"/api/v1/inventory/items/{item}/lots");
        Assert.Equal(2, lots!.Count);
        Assert.Contains(lots, l => l.UnitCostMinorUnits == 2400 && l.QuantityReceived == 500m);
        Assert.Contains(lots, l => l.UnitCostMinorUnits == 2500 && l.QuantityReceived == 250.5m);

        var movements = await client.GetFromJsonAsync<List<MovementDto>>($"/api/v1/inventory/items/{item}/movements");
        Assert.Equal(2, movements!.Count);
        Assert.All(movements, m => Assert.Equal("Purchase", m.Type));
        Assert.Equal(750.5m, movements.Sum(m => m.QuantityDelta));
    }

    [Fact]
    public async Task Purchase_WithoutCostAnywhere_Returns422_AndFutureDate_Rejected()
    {
        var client = await ClientAsync();
        var item = await CreateItemAsync(client, "No-cost pellets", defaultCost: null);

        var noCost = await client.PostWithKeyAsync(
            $"/api/v1/inventory/items/{item}/purchases", Guid.NewGuid().ToString(),
            new { receivedDate = Today, quantity = 10m, unitCostMinorUnits = (long?)null });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noCost.StatusCode);

        var future = await client.PostWithKeyAsync(
            $"/api/v1/inventory/items/{item}/purchases", Guid.NewGuid().ToString(),
            new { receivedDate = Today.AddDays(1), quantity = 10m, unitCostMinorUnits = 100 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, future.StatusCode);

        // Malformed body (unparseable date) is a 400, not a 500 — minimal-API
        // binding throws BadHttpRequestException, now mapped in /error.
        var malformed = await client.PostWithKeyAsync(
            $"/api/v1/inventory/items/{item}/purchases", Guid.NewGuid().ToString(),
            new { receivedDate = "", quantity = 10m, unitCostMinorUnits = 100 });
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    }

    [Fact]
    public async Task DuplicateName_IsCaseInsensitive409()
    {
        var client = await ClientAsync();
        await CreateItemAsync(client, "Grower Mash");

        var duplicate = await client.PostWithKeyAsync("/api/v1/inventory/items", Guid.NewGuid().ToString(),
            new { name = "  grower mash ", category = "Feed", unit = "kg", defaultUnitCostMinorUnits = (long?)null });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task Unit_EditableUntilFirstLot_ThenLocked()
    {
        var client = await ClientAsync();
        var item = await CreateItemAsync(client, "Scratch grains", unit: "kg");

        // No lots yet: unit edits are fine (typo fixing).
        var early = await PutWithKeyAsync(client, $"/api/v1/inventory/items/{item}",
            new { name = "Scratch grains", unit = "bags", defaultUnitCostMinorUnits = 2500 });
        Assert.Equal(HttpStatusCode.NoContent, early.StatusCode);

        (await client.PostWithKeyAsync(
            $"/api/v1/inventory/items/{item}/purchases", Guid.NewGuid().ToString(),
            new { receivedDate = Today, quantity = 5m, unitCostMinorUnits = 100 })).EnsureSuccessStatusCode();

        var locked = await PutWithKeyAsync(client, $"/api/v1/inventory/items/{item}",
            new { name = "Scratch grains", unit = "kg", defaultUnitCostMinorUnits = 2500 });
        Assert.Equal(HttpStatusCode.Conflict, locked.StatusCode);

        // Renaming without touching the unit still works.
        var rename = await PutWithKeyAsync(client, $"/api/v1/inventory/items/{item}",
            new { name = "Scratch grains (coarse)", unit = "bags", defaultUnitCostMinorUnits = 2500 });
        Assert.Equal(HttpStatusCode.NoContent, rename.StatusCode);
    }

    [Fact]
    public async Task Deactivate_HidesFromDefaultList_AndBlocksPurchases()
    {
        var client = await ClientAsync();
        var item = await CreateItemAsync(client, "Old winter mix");

        (await client.PostWithKeyAsync(
            $"/api/v1/inventory/items/{item}/deactivate", Guid.NewGuid().ToString()))
            .EnsureSuccessStatusCode();

        var defaults = await client.GetFromJsonAsync<List<ItemDto>>("/api/v1/inventory/items");
        Assert.DoesNotContain(defaults!, i => i.Id == item);
        var all = await client.GetFromJsonAsync<List<ItemDto>>("/api/v1/inventory/items?includeInactive=true");
        Assert.Contains(all!, i => i.Id == item && !i.Active);

        var purchase = await client.PostWithKeyAsync(
            $"/api/v1/inventory/items/{item}/purchases", Guid.NewGuid().ToString(),
            new { receivedDate = Today, quantity = 10m, unitCostMinorUnits = 100 });
        Assert.Equal(HttpStatusCode.Conflict, purchase.StatusCode);

        (await client.PostWithKeyAsync(
            $"/api/v1/inventory/items/{item}/activate", Guid.NewGuid().ToString()))
            .EnsureSuccessStatusCode();
        var afterReactivate = await client.PostWithKeyAsync(
            $"/api/v1/inventory/items/{item}/purchases", Guid.NewGuid().ToString(),
            new { receivedDate = Today, quantity = 10m, unitCostMinorUnits = 100 });
        Assert.Equal(HttpStatusCode.Created, afterReactivate.StatusCode);
    }

    // AGENTS.md Version-token rule: concurrent edits of the same item must not
    // silently lose one write. Version DELTA asserted, not an absolute.
    [Fact]
    public async Task ParallelItemUpdates_ExactlyOneWins()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        var item = await CreateItemAsync(client, "Race pellets");

        var versionBefore = await factory.WithTenantScopeAsync(accountId, async db =>
            (await db.InventoryItems.FirstAsync(i => i.Id == item)).Version);

        var a = PutWithKeyAsync(client, $"/api/v1/inventory/items/{item}",
            new { name = "Race pellets A", unit = "kg", defaultUnitCostMinorUnits = 100 });
        var b = PutWithKeyAsync(client, $"/api/v1/inventory/items/{item}",
            new { name = "Race pellets B", unit = "kg", defaultUnitCostMinorUnits = 200 });
        var responses = await Task.WhenAll(a, b);

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.NoContent));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        var versionAfter = await factory.WithTenantScopeAsync(accountId, async db =>
            (await db.InventoryItems.FirstAsync(i => i.Id == item)).Version);
        Assert.Equal(versionBefore + 1, versionAfter);
    }
}
