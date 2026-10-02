using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Cluckwork.Api.IntegrationTests;

// RecordFeedUsage locks the item, then reads flock eligibility, then locks the
// FIFO lots, in one transaction. A held row lock parks the usage request at one
// step while an archive commits, so the outcome shows which side of that step
// the eligibility read sits on.
[Collection(IntegrationCollection.Name)]
public sealed class FeedUsageLockOrderTests(CluckworkWebApplicationFactory factory)
{
    private sealed record Created(Guid Id);
    private sealed record LotCreated(Guid LotId);

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

    // #1022: the read before the transaction used to leave the flock tracked, so
    // the read after the item lock got that stale instance back and missed the
    // archive.
    [Fact]
    public async Task FlockArchivedWhileUsageWaitsForTheItemLock_IsRefused()
    {
        var (client, accountId, flockId, itemId, lotId) = await SetupAsync();

        await using var holder = await HoldAsync(accountId,
            db => db.Database.ExecuteSqlInterpolatedAsync(
                $"""SELECT 1 FROM "InventoryItems" WHERE "Id" = {itemId} FOR UPDATE"""));
        var usage = RecordUsageAsync(client, flockId, itemId);
        Assert.True(await factory.WaitUntilDoneOrBlockedAsync(usage, holder.Pid),
            "the usage request must park on the item lock");

        await ArchiveAsync(accountId, flockId);
        await holder.Transaction.CommitAsync();

        var response = await usage;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("FeedUsage.FlockNotActive",
            (await response.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
        Assert.Equal((100m, 0), await LotAndUsagesAsync(accountId, lotId, flockId));
    }

    private async Task<(HttpClient Client, Guid AccountId, Guid FlockId, Guid ItemId, Guid LotId)> SetupAsync()
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var flockId = await factory.SeedFlockAsync(accountId, Guid.NewGuid());
        var client = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(email));

        var item = await client.PostWithKeyAsync("/api/v1/inventory/items", Guid.NewGuid().ToString(),
            new { name = "Layer feed", category = "Feed", unit = "kg", defaultUnitCostMinorUnits = 2500 });
        item.EnsureSuccessStatusCode();
        var itemId = (await item.Content.ReadFromJsonAsync<Created>())!.Id;

        var purchase = await client.PostWithKeyAsync(
            $"/api/v1/inventory/items/{itemId}/purchases", Guid.NewGuid().ToString(),
            new { receivedDate = Today.AddDays(-1), quantity = 100m, unitCostMinorUnits = 2500 });
        purchase.EnsureSuccessStatusCode();
        var lotId = (await purchase.Content.ReadFromJsonAsync<LotCreated>())!.LotId;
        return (client, accountId, flockId, itemId, lotId);
    }

    private static Task<HttpResponseMessage> RecordUsageAsync(HttpClient client, Guid flockId, Guid itemId) =>
        client.PostWithKeyAsync($"/api/v1/inventory/items/{itemId}/usage", Guid.NewGuid().ToString(),
            new { flockId, date = Today, quantity = 5m });

    // Built directly rather than from factory.Services: that context retries on
    // failure, which forbids a hand-begun transaction (#269).
    private async Task<Holder> HoldAsync(Guid accountId, Func<AppDbContext, Task> takeLock)
    {
        var tenant = new TenantContext();
        tenant.Resolve(accountId);
        var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(factory.ConnectionString).Options,
            tenant, new FlockScope());
        var transaction = await db.Database.BeginTransactionAsync();
        await takeLock(db);
        return new Holder(db, transaction, await db.BackendPidAsync());
    }

    private Task ArchiveAsync(Guid accountId, Guid flockId) =>
        factory.WithTenantScopeAsync(accountId, async db =>
        {
            var flock = await db.Flocks.SingleAsync(f => f.Id == flockId);
            Assert.True(flock.Archive(Today).IsSuccess);
            await db.SaveChangesAsync();
        });

    private Task<(decimal Available, int Usages)> LotAndUsagesAsync(Guid accountId, Guid lotId, Guid flockId) =>
        factory.WithTenantScopeAsync(accountId, async db => (
            (await db.InventoryLots.SingleAsync(l => l.Id == lotId)).QuantityAvailable,
            await db.FeedUsages.CountAsync(u => u.FlockId == flockId)));

    private sealed record Holder(AppDbContext Db, IDbContextTransaction Transaction, int Pid) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Transaction.DisposeAsync();
            await Db.DisposeAsync();
        }
    }
}
