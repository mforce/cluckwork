namespace Cluckwork.Api.IntegrationTests;

using System.Net;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Inventory;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

// #313 — SalesOrderRepository.GetByIdLockedAsync and InventoryItemRepository.GetByIdLockedAsync
// select by Id alone and only check AccountId AFTER the row loads, because the raw FOR UPDATE
// SQL bypasses EF's tenant query filter. A caller authenticated as tenant A but supplying tenant
// B's row id therefore reaches Postgres's FOR UPDATE for B's row before the ownership check runs
// — and parks behind any lock tenant B is legitimately holding on it.
//
// These tests hold a raw FOR UPDATE lock on tenant B's row (simulating B's own in-flight
// transaction) and drive tenant A's real HTTP request at the same row id. Blocking is detected
// via pg_blocking_pids against the holder's backend pid — the same technique
// CurrencyLockRaceTests uses — so this is a positive assertion that A's request never even
// attempts B's row lock, not a timing guess.
[Collection(IntegrationCollection.Name)]
public sealed class TenantScopedLockTests(CluckworkWebApplicationFactory factory)
{
    [Fact]
    public async Task VoidSale_ForOtherTenantsOrder_DoesNotBlockOnTheOwningTenantsHeldLock()
    {
        var userA = $"a-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(userA);
        var accountB = await factory.SeedAccountWithUserAsync($"b-{Guid.NewGuid():N}@test.local");

        var gradesB = await factory.SeedEggGradesAsync(accountB, Guid.NewGuid(), "Large");
        var orderId = await factory.SeedSalesOrderAsync(accountB, gradesB["Large"], 10);

        // B holds an open FOR UPDATE transaction on its own order row — the
        // legitimate case this lock exists to serialize against. Built
        // directly (not via factory.Services — #269: that DbContext now
        // carries EnableRetryOnFailure, which forbids a manually-begun
        // transaction unless it's driven through
        // database.CreateExecutionStrategy().ExecuteAsync; this test needs
        // precise, uninterrupted control of a hand-held lock across several
        // sequential steps, which retry-as-a-whole-unit can't express) —
        // same "own plain, non-retrying AppDbContext" pattern
        // ReportQueryBoundingTests/StepUpAuthTests use for exactly this
        // reason.
        var holderTenant = new TenantContext();
        holderTenant.Resolve(accountB);
        await using var holderDb = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(factory.ConnectionString).Options,
            holderTenant, new FlockScope());
        await using var holderTx = await holderDb.Database.BeginTransactionAsync();
        await holderDb.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM "SalesOrders" WHERE "Id" = {orderId} FOR UPDATE""");
        var holderPid = await holderDb.BackendPidAsync();

        // A, fully authenticated for its OWN tenant, tries to void B's order id.
        var clientA = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(userA));
        var voidTask = clientA.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/void", Guid.NewGuid().ToString(), new { reason = "cross-tenant probe" });

        var blocked = await factory.WaitUntilDoneOrBlockedAsync(voidTask, holderPid);
        Assert.False(blocked,
            "tenant A's request must not park behind tenant B's row lock — the raw SQL predicate " +
            "must exclude B's row for A's tenant before FOR UPDATE is attempted");

        var response = await voidTask;
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await holderTx.RollbackAsync();
    }

    [Fact]
    public async Task UpdateInventoryItem_ForOtherTenantsItem_DoesNotBlockOnTheOwningTenantsHeldLock()
    {
        var userA = $"a-{Guid.NewGuid():N}@test.local";
        await factory.SeedAccountWithUserAsync(userA);
        var accountB = await factory.SeedAccountWithUserAsync($"b-{Guid.NewGuid():N}@test.local");

        var itemId = Guid.NewGuid();
        await factory.WithTenantScopeAsync(accountB, async db =>
        {
            db.InventoryItems.Add(InventoryItem.Create(
                itemId, accountB, SeedDefaults.FarmId, "B's Feed",
                InventoryCategory.Feed, "kg", defaultUnitCost: null));
            await db.SaveChangesAsync();
        });

        // B holds an open FOR UPDATE transaction on its own item row. Built
        // directly, not via factory.Services — see the #269 comment on the
        // sibling test above.
        var holderTenant = new TenantContext();
        holderTenant.Resolve(accountB);
        await using var holderDb = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(factory.ConnectionString).Options,
            holderTenant, new FlockScope());
        await using var holderTx = await holderDb.Database.BeginTransactionAsync();
        await holderDb.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM "InventoryItems" WHERE "Id" = {itemId} FOR UPDATE""");
        var holderPid = await holderDb.BackendPidAsync();

        // A, fully authenticated for its OWN tenant (and Admin, as UpdateItem
        // requires), tries to update B's item id.
        var clientA = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(userA));
        var updateTask = clientA.PutWithKeyAsync(
            $"/api/v1/inventory/items/{itemId}", Guid.NewGuid().ToString(),
            new { name = "Hijacked", unit = "kg", defaultUnitCostMinorUnits = (long?)null });

        var blocked = await factory.WaitUntilDoneOrBlockedAsync(updateTask, holderPid);
        Assert.False(blocked,
            "tenant A's request must not park behind tenant B's row lock — the raw SQL predicate " +
            "must exclude B's row for A's tenant before FOR UPDATE is attempted");

        var response = await updateTask;
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await holderTx.RollbackAsync();
    }
}
