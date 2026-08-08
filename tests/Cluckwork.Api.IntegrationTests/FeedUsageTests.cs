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
    private sealed record MovementWithRef(Guid Id, string Type, decimal QuantityDelta, string? ReferenceType, Guid? ReferenceId);
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

    // A backdated usage may only consume stock that existed on that day —
    // lots received later are invisible to it (codex review of PR #70).
    [Fact]
    public async Task BackdatedUsage_CannotConsumeLotsReceivedLater()
    {
        var (client, _, flockId, itemId) = await SetupAsync();
        await PurchaseAsync(client, itemId, 20m, 2500, Today.AddDays(-10));
        await PurchaseAsync(client, itemId, 100m, 2500, Today); // didn't exist a week ago

        var response = await client.PostWithKeyAsync(
            $"/api/v1/inventory/items/{itemId}/usage", Guid.NewGuid().ToString(),
            new { flockId, date = Today.AddDays(-7), quantity = 50m });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var ok = await client.PostWithKeyAsync(
            $"/api/v1/inventory/items/{itemId}/usage", Guid.NewGuid().ToString(),
            new { flockId, date = Today.AddDays(-7), quantity = 15m });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    // Non-feed catalog items can't be recorded as flock feed.
    [Fact]
    public async Task Usage_NonFeedCategory_Returns422()
    {
        var (client, _, flockId, _) = await SetupAsync();
        var packaging = await client.PostWithKeyAsync("/api/v1/inventory/items", Guid.NewGuid().ToString(),
            new { name = "Egg cartons", category = "Packaging", unit = "pcs", defaultUnitCostMinorUnits = 50 });
        packaging.EnsureSuccessStatusCode();
        var packagingId = (await packaging.Content.ReadFromJsonAsync<Created>())!.Id;
        await PurchaseAsync(client, packagingId, 500m, 50, Today);

        var response = await client.PostWithKeyAsync(
            $"/api/v1/inventory/items/{packagingId}/usage", Guid.NewGuid().ToString(),
            new { flockId, date = Today, quantity = 10m });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // Usage movements carry a reference to their FeedUsage record so same-day
    // feedings of the same flock reconcile individually.
    [Fact]
    public async Task UsageMovements_ReferenceTheirUsageRecord()
    {
        var (client, _, flockId, itemId) = await SetupAsync();
        await PurchaseAsync(client, itemId, 100m, 2500, Today);

        var first = await client.PostWithKeyAsync(
            $"/api/v1/inventory/items/{itemId}/usage", Guid.NewGuid().ToString(),
            new { flockId, date = Today, quantity = 10m });
        var second = await client.PostWithKeyAsync(
            $"/api/v1/inventory/items/{itemId}/usage", Guid.NewGuid().ToString(),
            new { flockId, date = Today, quantity = 15m });
        var firstId = (await first.Content.ReadFromJsonAsync<UsageResponse>())!.FeedUsageId;
        var secondId = (await second.Content.ReadFromJsonAsync<UsageResponse>())!.FeedUsageId;

        var movements = await client.GetFromJsonAsync<List<MovementWithRef>>(
            $"/api/v1/inventory/items/{itemId}/movements");
        var usageRows = movements!.Where(m => m.Type == "Usage").ToList();
        Assert.Equal(2, usageRows.Count);
        Assert.Contains(usageRows, m => m.ReferenceId == firstId && m.QuantityDelta == -10m);
        Assert.Contains(usageRows, m => m.ReferenceId == secondId && m.QuantityDelta == -15m);
        Assert.All(usageRows, m => Assert.Equal("FeedUsage", m.ReferenceType));
    }

    // Corrections can't predate the stock they correct.
    [Fact]
    public async Task Adjustment_BeforeLotReceipt_Returns422()
    {
        var (client, _, _, itemId) = await SetupAsync();
        var lot = await PurchaseAsync(client, itemId, 100m, 2500, Today.AddDays(-2));

        var response = await client.PostWithKeyAsync(
            $"/api/v1/inventory/items/{itemId}/adjustments", Guid.NewGuid().ToString(),
            new { inventoryLotId = lot, date = Today.AddDays(-5), type = "Adjustment", quantityDelta = -5m, reason = "impossible date" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
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

    // -----------------------------------------------------------------------
    // #446 — record-time DailyEntryId stamping. The contract, exactly: "the
    // non-voided daily entry that existed for this flock's (farm, house,
    // flock, date) when the row was recorded". No backfill — a row recorded
    // before the day's entry exists stays null forever; flock+date remains
    // the authoritative join.
    // -----------------------------------------------------------------------

    private sealed record UsageRowWithEntry(Guid Id, DateOnly Date, Guid? DailyEntryId);
    private sealed record EntryVersionDto(Guid Id, int Version);

    private async Task<(HttpClient Client, Guid AccountId, Guid FlockId, Guid FarmId, Guid HouseId, Guid ItemId)>
        SetupWithKnownHouseAsync()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var farmId = Guid.NewGuid();
        var houseId = Guid.NewGuid();
        var flockId = await factory.SeedFlockAsync(accountId, farmId, houseId);
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        var item = await client.PostWithKeyAsync("/api/v1/inventory/items", Guid.NewGuid().ToString(),
            new { name = "Layer feed", category = "Feed", unit = "kg", defaultUnitCostMinorUnits = 2500 });
        item.EnsureSuccessStatusCode();
        var itemId = (await item.Content.ReadFromJsonAsync<Created>())!.Id;
        await PurchaseAsync(client, itemId, 500m, 2500, Today.AddDays(-10));
        return (client, accountId, flockId, farmId, houseId, itemId);
    }

    private static async Task<Guid> RecordEmptyDraftEntryAsync(
        HttpClient client, Guid farmId, Guid houseId, Guid flockId, DateOnly date)
    {
        var record = await client.PostWithKeyAsync("/api/v1/daily-entries", Guid.NewGuid().ToString(), new
        {
            farmId, houseId, flockId, date,
            totalEggs = 0, crackedEggs = 0, dirtyEggs = 0, discardedEggs = 0, mortalityCount = 0,
            grades = Array.Empty<object>(),
        });
        record.EnsureSuccessStatusCode();
        return (await record.Content.ReadFromJsonAsync<Created>())!.Id;
    }

    private async Task<Guid?> RecordUsageAndReadLinkAsync(
        HttpClient client, Guid itemId, Guid flockId, DateOnly date)
    {
        var response = await client.PostWithKeyAsync(
            $"/api/v1/inventory/items/{itemId}/usage", Guid.NewGuid().ToString(),
            new { flockId, date, quantity = 5m });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = await client.GetFromJsonAsync<List<UsageRowWithEntry>>(
            $"/api/v1/inventory/usage?flockId={flockId}&from={date:yyyy-MM-dd}&to={date:yyyy-MM-dd}");
        return Assert.Single(rows!).DailyEntryId;
    }

    [Fact]
    public async Task Usage_WithExistingDailyEntry_StampsTheLink()
    {
        var (client, _, flockId, farmId, houseId, itemId) = await SetupWithKnownHouseAsync();
        var entryId = await RecordEmptyDraftEntryAsync(client, farmId, houseId, flockId, Today);

        Assert.Equal(entryId, await RecordUsageAndReadLinkAsync(client, itemId, flockId, Today));
    }

    [Fact]
    public async Task Usage_NoDailyEntryYet_LinkStaysNull_AndIsNeverBackfilled()
    {
        var (client, _, flockId, farmId, houseId, itemId) = await SetupWithKnownHouseAsync();

        Assert.Null(await RecordUsageAndReadLinkAsync(client, itemId, flockId, Today));

        // The day's entry arriving later does NOT rewrite history — no
        // backfill, by design (grilled out of #446: the backfill coupled the
        // usage rows' concurrency tokens to the entry save).
        await RecordEmptyDraftEntryAsync(client, farmId, houseId, flockId, Today);
        var rows = await client.GetFromJsonAsync<List<UsageRowWithEntry>>(
            $"/api/v1/inventory/usage?flockId={flockId}");
        Assert.Null(Assert.Single(rows!).DailyEntryId);
    }

    [Fact]
    public async Task Usage_VoidedEntryIsNotLinked_ARecreatedEntryIs()
    {
        var (client, accountId, flockId, farmId, houseId, itemId) = await SetupWithKnownHouseAsync();

        // Submit a real graded entry then void it — the natural-key slot is
        // vacated (#82) and the voided entry must never be linked.
        var grades = await factory.SeedEggGradesAsync(accountId, farmId, "Large");
        var record = await client.PostWithKeyAsync("/api/v1/daily-entries", Guid.NewGuid().ToString(), new
        {
            farmId, houseId, flockId, date = Today,
            totalEggs = 90, crackedEggs = 0, dirtyEggs = 0, discardedEggs = 0, mortalityCount = 0,
            grades = new[] { new { eggGradeId = grades["Large"], quantity = 90 } },
        });
        record.EnsureSuccessStatusCode();
        var voidedId = (await record.Content.ReadFromJsonAsync<Created>())!.Id;
        (await client.PostWithKeyAsync(
            $"/api/v1/daily-entries/{voidedId}/submit", Guid.NewGuid().ToString())).EnsureSuccessStatusCode();
        var version = (await client.GetFromJsonAsync<EntryVersionDto>(
            $"/api/v1/daily-entries/{voidedId}"))!.Version;
        (await client.PostWithKeyAsync(
            $"/api/v1/daily-entries/{voidedId}/void", Guid.NewGuid().ToString(),
            new { version, reason = "test void" })).EnsureSuccessStatusCode();

        Assert.Null(await RecordUsageAndReadLinkAsync(client, itemId, flockId, Today));

        // A fresh entry re-recorded into the vacated slot links NEW rows only.
        var recreatedId = await RecordEmptyDraftEntryAsync(client, farmId, houseId, flockId, Today);
        var response = await client.PostWithKeyAsync(
            $"/api/v1/inventory/items/{itemId}/usage", Guid.NewGuid().ToString(),
            new { flockId, date = Today, quantity = 3m });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = await client.GetFromJsonAsync<List<UsageRowWithEntry>>(
            $"/api/v1/inventory/usage?flockId={flockId}");
        Assert.Equal(2, rows!.Count);
        Assert.Contains(rows, r => r.DailyEntryId == null);          // pre-recreate row, untouched
        Assert.Contains(rows, r => r.DailyEntryId == recreatedId);   // post-recreate row, linked
    }
}
