namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

// #60 — void a confirmed order: the allocated quantities return to the exact
// egg lots they were drawn from (allocation provenance recorded at confirm),
// FIFO order is preserved for the next sale, and racing mutations serialize on
// the FOR UPDATE lot locks + the order's Version concurrency token.
[Collection(IntegrationCollection.Name)]
public sealed class VoidSaleTests(CluckworkWebApplicationFactory factory)
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

    private static async Task<HttpResponseMessage> VoidAsync(
        HttpClient client, Guid orderId, string reason = "Confirmed by mistake") =>
        await client.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/void", Guid.NewGuid().ToString(), new { reason });

    [Fact]
    public async Task Void_RestoresExactSourceLots_AndPreservesFifo()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var grades = await factory.SeedEggGradesAsync(accountId, Guid.NewGuid(), "Large");

        // Older lot 30, newer lot 100. An order of 50 drains the older lot
        // fully (FIFO) and takes 20 from the newer.
        var olderLot = await factory.SeedEggLotAsync(
            accountId, grades["Large"], 30, productionDate: Today.AddDays(-5));
        var newerLot = await factory.SeedEggLotAsync(
            accountId, grades["Large"], 100, productionDate: Today);
        var order = await factory.SeedSalesOrderAsync(accountId, grades["Large"], 50);

        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        (await client.PostWithKeyAsync($"/api/v1/sales/{order}/confirm", Guid.NewGuid().ToString()))
            .EnsureSuccessStatusCode();

        var afterConfirm = await LotQuantitiesAsync(accountId, olderLot, newerLot);
        Assert.Equal((0, 80), afterConfirm);

        var voidResponse = await VoidAsync(client, order);
        Assert.Equal(HttpStatusCode.OK, voidResponse.StatusCode);

        // Exact lot-level restore — not just the grade total.
        var afterVoid = await LotQuantitiesAsync(accountId, olderLot, newerLot);
        Assert.Equal((30, 100), afterVoid);

        // Provenance survives the void (spec §9.6 traceability): rows are kept,
        // marked released — never deleted.
        var (total, released) = await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var rows = await db.SalesOrderAllocations.Where(a => a.SalesOrderId == order).ToListAsync();
            return (rows.Count, rows.Count(r => r.ReleasedOnUtc != null));
        });
        Assert.Equal(2, total);       // one row per source lot
        Assert.Equal(total, released);

        // Voided order stays listed, distinct status, lines/total intact.
        var voided = await client.GetFromJsonAsync<OrderDto>($"/api/v1/sales/{order}");
        Assert.Equal("Voided", voided!.Status);
        Assert.Equal("Confirmed by mistake", voided.VoidReason);
        Assert.Single(voided.Items);

        // FIFO preserved: the next sale draws from the restored older lot first.
        var nextOrder = await factory.SeedSalesOrderAsync(accountId, grades["Large"], 10);
        (await client.PostWithKeyAsync($"/api/v1/sales/{nextOrder}/confirm", Guid.NewGuid().ToString()))
            .EnsureSuccessStatusCode();
        var afterNext = await LotQuantitiesAsync(accountId, olderLot, newerLot);
        Assert.Equal((20, 100), afterNext);
    }

    // Multi-grade orders exercise the single-statement lock acquisition on the
    // confirm side (one FOR UPDATE across all grades, canonical order) and the
    // cross-grade restore on the void side.
    [Fact]
    public async Task Void_MultiGradeOrder_RestoresEveryGrade()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var grades = await factory.SeedEggGradesAsync(accountId, Guid.NewGuid(), "Large", "Medium");
        var largeLot = await factory.SeedEggLotAsync(accountId, grades["Large"], 60);
        var mediumLot = await factory.SeedEggLotAsync(accountId, grades["Medium"], 40);
        var order = await factory.SeedSalesOrderAsync(
            accountId, [(grades["Large"], 50), (grades["Medium"], 25)]);

        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        (await client.PostWithKeyAsync($"/api/v1/sales/{order}/confirm", Guid.NewGuid().ToString()))
            .EnsureSuccessStatusCode();
        Assert.Equal((10, 15), await LotQuantitiesAsync(accountId, largeLot, mediumLot));

        Assert.Equal(HttpStatusCode.OK, (await VoidAsync(client, order)).StatusCode);
        Assert.Equal((60, 40), await LotQuantitiesAsync(accountId, largeLot, mediumLot));
    }

    [Fact]
    public async Task Void_Draft_Returns409_AndVoidTwice_Returns409()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var grades = await factory.SeedEggGradesAsync(accountId, Guid.NewGuid(), "Large");
        await factory.SeedEggLotAsync(accountId, grades["Large"], 100);

        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        var draft = await factory.SeedSalesOrderAsync(accountId, grades["Large"], 10);
        Assert.Equal(HttpStatusCode.Conflict, (await VoidAsync(client, draft)).StatusCode);

        var order = await factory.SeedSalesOrderAsync(accountId, grades["Large"], 10);
        (await client.PostWithKeyAsync($"/api/v1/sales/{order}/confirm", Guid.NewGuid().ToString()))
            .EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await VoidAsync(client, order)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await VoidAsync(client, order)).StatusCode);
    }

    [Fact]
    public async Task Void_WithoutReason_Returns400()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var grades = await factory.SeedEggGradesAsync(accountId, Guid.NewGuid(), "Large");
        await factory.SeedEggLotAsync(accountId, grades["Large"], 100);
        var order = await factory.SeedSalesOrderAsync(accountId, grades["Large"], 10);

        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        (await client.PostWithKeyAsync($"/api/v1/sales/{order}/confirm", Guid.NewGuid().ToString()))
            .EnsureSuccessStatusCode();

        var response = await VoidAsync(client, order, reason: "   ");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Void_OrderConfirmedBeforeProvenanceExisted_Returns422()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var grades = await factory.SeedEggGradesAsync(accountId, Guid.NewGuid(), "Large");
        await factory.SeedEggLotAsync(accountId, grades["Large"], 100);
        var order = await factory.SeedSalesOrderAsync(accountId, grades["Large"], 10);

        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        (await client.PostWithKeyAsync($"/api/v1/sales/{order}/confirm", Guid.NewGuid().ToString()))
            .EnsureSuccessStatusCode();

        // Simulate a pre-#60 order: confirmed, but no allocation rows.
        await factory.WithTenantScopeAsync(accountId, async db =>
            await db.SalesOrderAllocations
                .Where(a => a.SalesOrderId == order)
                .ExecuteDeleteAsync());

        var response = await VoidAsync(client, order);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        // Refused void must not touch stock or status.
        var status = await factory.WithTenantScopeAsync(accountId, async db =>
            (await db.SalesOrders.FirstAsync(o => o.Id == order)).Status.ToString());
        Assert.Equal("Confirmed", status);
    }

    // AGENTS.md Version-token rule: racing voids of the same order must not
    // double-restore. The order row is locked FOR UPDATE inside the void
    // transaction, so the loser blocks, re-reads Voided, and deterministically
    // gets SalesOrder.AlreadyVoided → 409.
    [Fact]
    public async Task ParallelVoids_ExactlyOneWins_StockRestoredOnce()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var grades = await factory.SeedEggGradesAsync(accountId, Guid.NewGuid(), "Large");
        var lot = await factory.SeedEggLotAsync(accountId, grades["Large"], 100);
        var order = await factory.SeedSalesOrderAsync(accountId, grades["Large"], 40);

        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        (await client.PostWithKeyAsync($"/api/v1/sales/{order}/confirm", Guid.NewGuid().ToString()))
            .EnsureSuccessStatusCode();

        var versionBefore = await factory.WithTenantScopeAsync(accountId, async db =>
            (await db.SalesOrders.FirstAsync(o => o.Id == order)).Version);

        var responses = await Task.WhenAll(
            VoidAsync(client, order, "race A"), VoidAsync(client, order, "race B"));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        var (available, versionAfter) = await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var lotRow = await db.EggLots.FirstAsync(l => l.Id == lot);
            var orderRow = await db.SalesOrders.FirstAsync(o => o.Id == order);
            return (lotRow.QuantityAvailable, orderRow.Version);
        });

        // Restored exactly once — never 140, never still 60.
        Assert.Equal(100, available);
        // Version DELTA, not absolute: exactly one void committed.
        Assert.Equal(versionBefore + 1, versionAfter);
    }

    // A void racing a confirm that wants the same stock: the FOR UPDATE lot
    // locks serialize them. Whichever order wins, the books balance — stock is
    // either fully restored (confirm lost) or fully reallocated (confirm won).
    [Fact]
    public async Task VoidRacingConfirm_OnSameLots_NeverLosesOrDuplicatesStock()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var grades = await factory.SeedEggGradesAsync(accountId, Guid.NewGuid(), "Large");
        var lot = await factory.SeedEggLotAsync(accountId, grades["Large"], 100);

        var orderA = await factory.SeedSalesOrderAsync(accountId, grades["Large"], 100);
        var orderB = await factory.SeedSalesOrderAsync(accountId, grades["Large"], 100);

        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));
        (await client.PostWithKeyAsync($"/api/v1/sales/{orderA}/confirm", Guid.NewGuid().ToString()))
            .EnsureSuccessStatusCode();

        var voidA = VoidAsync(client, orderA);
        var confirmB = client.PostWithKeyAsync($"/api/v1/sales/{orderB}/confirm", Guid.NewGuid().ToString());
        var (voidResponse, confirmResponse) = (await voidA, await confirmB);

        // The void always succeeds; the confirm depends on arrival order —
        // before the restore there is no stock (422), after it there is (200).
        Assert.Equal(HttpStatusCode.OK, voidResponse.StatusCode);

        var available = await factory.WithTenantScopeAsync(accountId, async db =>
            (await db.EggLots.FirstAsync(l => l.Id == lot)).QuantityAvailable);

        if (confirmResponse.StatusCode == HttpStatusCode.OK)
            Assert.Equal(0, available);   // restored 100, then B took 100
        else
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, confirmResponse.StatusCode);
            Assert.Equal(100, available); // B saw an empty lot; restore stands
        }
    }

    private Task<(int, int)> LotQuantitiesAsync(Guid accountId, Guid lotA, Guid lotB) =>
        factory.WithTenantScopeAsync(accountId, async db =>
        {
            var a = await db.EggLots.FirstAsync(l => l.Id == lotA);
            var b = await db.EggLots.FirstAsync(l => l.Id == lotB);
            return (a.QuantityAvailable, b.QuantityAvailable);
        });

    private sealed record OrderDto(
        Guid Id, string Status, string? VoidReason, List<ItemDto> Items);

    private sealed record ItemDto(Guid Id, int Quantity);
}
