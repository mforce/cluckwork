using System.Net;
using System.Net.Http.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Eggs;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Api.IntegrationTests;

// #857 — ConfirmSaleHandler reads a plain Worker's flock assignments inside its
// transaction, after the account and order locks (#612, #727).
// SaleAllocationPolicyTests pins that timing for the role and the allocation
// policy; this pins it for the assignments, which IAccessLookup now serves. The
// confirm parks on the account lock while another transaction removes the
// worker's only assignment, so it must allocate as an unrestricted worker.
[Collection(IntegrationCollection.Name)]
public sealed class ConfirmSaleAssignmentReadTests(CluckworkWebApplicationFactory factory)
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
    private sealed record Created(Guid Id);

    private async Task<Guid> SeedLotAsync(Guid accountId, Guid flockId, Guid gradeId, int quantity, DateOnly date)
    {
        var lotId = Guid.NewGuid();
        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            db.EggLots.Add(EggLot.Create(lotId, accountId, flockId, date, gradeId, quantity));
            db.EggInventoryMovements.Add(EggInventoryMovement.Create(
                Guid.NewGuid(), accountId, lotId, EggMovementType.Production, quantity, "DailyEntry", Guid.NewGuid()));
            await db.SaveChangesAsync();
        });
        return lotId;
    }

    [Fact]
    public async Task ConfirmSale_ParkedOnTheAccountLock_ReadsAnAssignmentRemovalThatCommittedWhileItWaited()
    {
        var ownerEmail = $"o-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(ownerEmail);
        var owner = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(ownerEmail));
        var farmId = Guid.NewGuid();
        var gradeId = (await factory.SeedEggGradesAsync(accountId, farmId, "Large"))["Large"];
        var productId = await factory.SeedProductAsync(accountId, farmId, gradeId, "Large Eggs", 100);
        var flockA = await factory.SeedFlockAsync(accountId, farmId);
        var flockB = await factory.SeedFlockAsync(accountId, farmId);
        // The assigned flock alone cannot fill 10; the farm can. Under the
        // default AssignedFlocksOnly policy, only an unrestricted read confirms.
        var lotA = await SeedLotAsync(accountId, flockA, gradeId, 5, Today.AddDays(-1));
        var lotB = await SeedLotAsync(accountId, flockB, gradeId, 20, Today);

        var workerEmail = $"w-{Guid.NewGuid():N}@test.local";
        await factory.SeedUserAsync(accountId, workerEmail, (string?)null);
        var workerId = await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var id = await db.Users.Where(u => u.Email == workerEmail).Select(u => u.Id).SingleAsync();
            db.UserRoleAssignments.Add(UserRoleAssignment.Create(
                Guid.NewGuid(), accountId, id, farmId: null, houseId: null, flockA));
            await db.SaveChangesAsync();
            return id;
        });
        var worker = factory.CreateAuthedClient(await factory.LoginForAccessTokenAsync(workerEmail));

        var customer = await owner.PostWithKeyAsync("/api/v1/customers", Guid.NewGuid().ToString(),
            new { name = $"Buyer {Guid.NewGuid():N}"[..14], phone = "1" });
        var customerId = (await customer.Content.ReadFromJsonAsync<Created>())!.Id;
        var order = await owner.PostWithKeyAsync("/api/v1/sales", Guid.NewGuid().ToString(),
            new { customerId, orderDate = Today });
        var orderId = (await order.Content.ReadFromJsonAsync<Created>())!.Id;
        Assert.Equal(HttpStatusCode.Created, (await owner.PostWithKeyAsync(
            $"/api/v1/sales/{orderId}/items", Guid.NewGuid().ToString(), new { productId, quantity = 10 })).StatusCode);

        // The fence holds the account row, as an uncommitted write would, and in
        // the same transaction removes the worker's only assignment. Built
        // directly, not through factory.Services (#269).
        var tenant = new TenantContext();
        tenant.Resolve(accountId);
        await using var fence = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(factory.ConnectionString).Options,
            tenant, new FlockScope());
        await using var transaction = await fence.Database.BeginTransactionAsync();
        await fence.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM "Accounts" WHERE "Id" = {accountId} FOR UPDATE""");
        await fence.Database.ExecuteSqlInterpolatedAsync(
            $"""DELETE FROM "UserRoleAssignments" WHERE "UserId" = {workerId}""");
        var holderPid = await fence.BackendPidAsync();

        var confirm = worker.PostWithKeyAsync($"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString());
        Assert.True(await factory.WaitUntilDoneOrBlockedAsync(confirm, holderPid),
            "ConfirmSaleHandler must park on the account row's shared lock before reading assignments");

        await transaction.CommitAsync();
        var response = await confirm;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var (quantityA, quantityB) = await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var lots = await db.EggLots.AsNoTracking().Where(l => l.Id == lotA || l.Id == lotB).ToListAsync();
            return (lots.Single(l => l.Id == lotA).QuantityAvailable, lots.Single(l => l.Id == lotB).QuantityAvailable);
        });
        Assert.Equal(0, quantityA);  // FIFO: the older lot first
        Assert.Equal(15, quantityB); // then the unassigned flock, so the removal was read
    }
}
