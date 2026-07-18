namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

// #66 (PR 2) — feed usage + corrections: FIFO lot consumption under the
// canonical lock, per-lot Usage ledger rows with lot-cost estimates, and the
// compensating Adjustment/Discard path that makes typo'd purchases fixable.
[Collection(IntegrationCollection.Name)]
public sealed class FeedUsageTests(CluckworkWebApplicationFactory factory)
{
    private sealed record Created(Guid Id);
    private sealed record LotCreated(Guid LotId);
    private sealed record UsageResponse(Guid FeedUsageId, decimal QuantityUsed, long EstimatedCostMinorUnits, string CurrencyCode);
    private sealed record UsageRow(Guid Id, Guid FlockId, DateOnly Date, decimal Quantity, string Unit, long EstimatedCostMinorUnits, string? Note);
    private sealed record MovementDto(Guid Id, Guid? InventoryLotId, DateOnly Date, string Type, decimal QuantityDelta);
    private sealed record ItemDto(Guid Id, decimal QuantityOnHand);

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

    private async Task<(HttpClient Client, Guid AccountId, Guid FlockId, Guid ItemId)> SetupAsync()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var flockId = await factory.SeedFlockAsync(accountId, Guid.NewGuid());
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        var item = await client.PostWithKeyAsync("/api/v1/inventory/items", Guid.NewGuid().ToString(),
            new { name = "Layer feed", category = "Feed", unit = "kg", defaultUnitCostMinorUnits = 2500 });
        item.EnsureSuccessStatusCode();
        var itemId = (await item.Content.ReadFromJsonAsync<Created>())!.Id;
        return (client, accountId, flockId, itemId);
    }

    private static async Task<Guid> PurchaseAsync(
        HttpClient client, Guid itemId, decimal quantity, long cost, DateOnly date)
    {
        var response = await client.PostWithKeyAsync(
            $"/api/v1/inventory/items/{itemId}/purchases", Guid.NewGuid().ToString(),
            new { receivedDate = date, quantity, unitCostMinorUnits = cost });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LotCreated>())!.LotId;
    }

    [Fact]
    public async Task Usage_DrainsFifo_WritesPerLotMovements_AndEstimatesLotCost()
    {
        var (client, accountId, flockId, itemId) = await SetupAsync();
        var olderLot = await PurchaseAsync(client, itemId, 30m, 2400, Today.AddDays(-5));
        var newerLot = await PurchaseAsync(client, itemId, 100m, 2500, Today);

        var response = await client.PostWithKeyAsync(
            $"/api/v1/inventory/items/{itemId}/usage", Guid.NewGuid().ToString(),
            new { flockId, date = Today, quantity = 50m, note = "morning feed" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var usage = await response.Content.ReadFromJsonAsync<UsageResponse>();

        // 30 kg @ 2400 + 20 kg @ 2500 = 122_000 minor units.
        Assert.Equal(122_000, usage!.EstimatedCostMinorUnits);

        var item = await client.GetFromJsonAsync<ItemDto>($"/api/v1/inventory/items/{itemId}");
        Assert.Equal(80m, item!.QuantityOnHand);

        // Exact lot-level draining: older emptied, newer partially consumed.
        var (olderAvail, newerAvail) = await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var older = await db.InventoryLots.FirstAsync(l => l.Id == olderLot);
            var newer = await db.InventoryLots.FirstAsync(l => l.Id == newerLot);
            return (older.QuantityAvailable, newer.QuantityAvailable);
        });
        Assert.Equal(0m, olderAvail);
        Assert.Equal(80m, newerAvail);

        // One Usage ledger row per lot drained (provenance).
        var movements = await client.GetFromJsonAsync<List<MovementDto>>(
            $"/api/v1/inventory/items/{itemId}/movements");
        var usageRows = movements!.Where(m => m.Type == "Usage").ToList();
        Assert.Equal(2, usageRows.Count);
        Assert.Contains(usageRows, m => m.InventoryLotId == olderLot && m.QuantityDelta == -30m);
        Assert.Contains(usageRows, m => m.InventoryLotId == newerLot && m.QuantityDelta == -20m);

        // Usage record listed with filters.
        var list = await client.GetFromJsonAsync<List<UsageRow>>(
            $"/api/v1/inventory/usage?flockId={flockId}");
        Assert.Single(list!);
        Assert.Equal(50m, list![0].Quantity);
        Assert.Equal("morning feed", list[0].Note);
    }

    [Fact]
    public async Task Usage_InsufficientStock_Returns422_AndChangesNothing()
    {
        var (client, accountId, flockId, itemId) = await SetupAsync();
        await PurchaseAsync(client, itemId, 20m, 2500, Today);

        var response = await client.PostWithKeyAsync(
            $"/api/v1/inventory/items/{itemId}/usage", Guid.NewGuid().ToString(),
            new { flockId, date = Today, quantity = 25m });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var item = await client.GetFromJsonAsync<ItemDto>($"/api/v1/inventory/items/{itemId}");
        Assert.Equal(20m, item!.QuantityOnHand);
        var usageCount = await factory.WithTenantScopeAsync(accountId, async db =>
            await db.FeedUsages.CountAsync());
        Assert.Equal(0, usageCount);
    }

    [Fact]
    public async Task Usage_ArchivedFlock_Returns422()
    {
        var (client, accountId, flockId, itemId) = await SetupAsync();
        await PurchaseAsync(client, itemId, 100m, 2500, Today);

        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var flock = await db.Flocks.FirstAsync(f => f.Id == flockId);
            flock.Deplete(Today.AddDays(-1));
            flock.Archive(Today);
            await db.SaveChangesAsync();
        });

        var response = await client.PostWithKeyAsync(
            $"/api/v1/inventory/items/{itemId}/usage", Guid.NewGuid().ToString(),
            new { flockId, date = Today, quantity = 10m });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Adjustment_FixesTypodPurchase_LedgerKeepsBothRows()
    {
        var (client, _, _, itemId) = await SetupAsync();
        // Fat-fingered 5000 instead of 500.
        var lot = await PurchaseAsync(client, itemId, 5000m, 2500, Today);

        var response = await client.PostWithKeyAsync(
            $"/api/v1/inventory/items/{itemId}/adjustments", Guid.NewGuid().ToString(),
            new { inventoryLotId = lot, date = Today, type = "Adjustment", quantityDelta = -4500m, reason = "typo: received 500, entered 5000" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var item = await client.GetFromJsonAsync<ItemDto>($"/api/v1/inventory/items/{itemId}");
        Assert.Equal(500m, item!.QuantityOnHand);

        var movements = await client.GetFromJsonAsync<List<MovementDto>>(
            $"/api/v1/inventory/items/{itemId}/movements");
        Assert.Contains(movements!, m => m.Type == "Purchase" && m.QuantityDelta == 5000m);
        Assert.Contains(movements!, m => m.Type == "Adjustment" && m.QuantityDelta == -4500m);
    }

    [Fact]
    public async Task Adjustment_Guards()
    {
        var (client, _, flockId, itemId) = await SetupAsync();
        var lot = await PurchaseAsync(client, itemId, 100m, 2500, Today);
        (await client.PostWithKeyAsync(
            $"/api/v1/inventory/items/{itemId}/usage", Guid.NewGuid().ToString(),
            new { flockId, date = Today, quantity = 70m })).EnsureSuccessStatusCode();

        // Below what's left (30 available) → 422, stock untouched.
        var tooDeep = await client.PostWithKeyAsync(
            $"/api/v1/inventory/items/{itemId}/adjustments", Guid.NewGuid().ToString(),
            new { inventoryLotId = lot, date = Today, type = "Adjustment", quantityDelta = -40m, reason = "count correction" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooDeep.StatusCode);

        // Positive beyond received (30 + 80 > 100) → 422.
        var beyond = await client.PostWithKeyAsync(
            $"/api/v1/inventory/items/{itemId}/adjustments", Guid.NewGuid().ToString(),
            new { inventoryLotId = lot, date = Today, type = "Adjustment", quantityDelta = 80m, reason = "found stock" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, beyond.StatusCode);

        // Positive discard → 400 (validator).
        var positiveDiscard = await client.PostWithKeyAsync(
            $"/api/v1/inventory/items/{itemId}/adjustments", Guid.NewGuid().ToString(),
            new { inventoryLotId = lot, date = Today, type = "Discard", quantityDelta = 5m, reason = "spoiled" });
        Assert.Equal(HttpStatusCode.BadRequest, positiveDiscard.StatusCode);

        // Whitespace reason → 400.
        var noReason = await client.PostWithKeyAsync(
            $"/api/v1/inventory/items/{itemId}/adjustments", Guid.NewGuid().ToString(),
            new { inventoryLotId = lot, date = Today, type = "Adjustment", quantityDelta = -5m, reason = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);

        // Valid positive restore within received works.
        var restore = await client.PostWithKeyAsync(
            $"/api/v1/inventory/items/{itemId}/adjustments", Guid.NewGuid().ToString(),
            new { inventoryLotId = lot, date = Today, type = "Adjustment", quantityDelta = 20m, reason = "usage overstated" });
        Assert.Equal(HttpStatusCode.Created, restore.StatusCode);
        var item = await client.GetFromJsonAsync<ItemDto>($"/api/v1/inventory/items/{itemId}");
        Assert.Equal(50m, item!.QuantityOnHand);
    }

    // AGENTS.md race rule: two usages racing one lot serialize on FOR UPDATE —
    // exactly one drains it, the loser gets a clean insufficient-stock 422,
    // the lot never goes negative, and its Version delta reflects one consume.
    [Fact]
    public async Task ParallelUsages_OneLot_NeverOversell()
    {
        var (client, accountId, flockId, itemId) = await SetupAsync();
        var lot = await PurchaseAsync(client, itemId, 100m, 2500, Today);

        var versionBefore = await factory.WithTenantScopeAsync(accountId, async db =>
            (await db.InventoryLots.FirstAsync(l => l.Id == lot)).Version);

        var responses = await Task.WhenAll(
            client.PostWithKeyAsync($"/api/v1/inventory/items/{itemId}/usage", Guid.NewGuid().ToString(),
                new { flockId, date = Today, quantity = 60m }),
            client.PostWithKeyAsync($"/api/v1/inventory/items/{itemId}/usage", Guid.NewGuid().ToString(),
                new { flockId, date = Today, quantity = 60m }));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity));

        var (available, versionAfter, usageCount) = await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var row = await db.InventoryLots.FirstAsync(l => l.Id == lot);
            var usages = await db.FeedUsages.CountAsync();
            return (row.QuantityAvailable, row.Version, usages);
        });
        Assert.Equal(40m, available);
        Assert.Equal(versionBefore + 1, versionAfter);
        Assert.Equal(1, usageCount);
    }
}
