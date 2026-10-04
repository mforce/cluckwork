using System.Net;
using System.Net.Http.Json;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Eggs;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Api.IntegrationTests;

// Both live authorization reads must follow the order lock as well as the account lock.
[Collection(IntegrationCollection.Name)]
public sealed class ConfirmSaleOrderBoundaryTests(CluckworkWebApplicationFactory factory)
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmReadsRoleAndAssignmentsAfterTheOrderLock(bool disableWorker)
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

        var tenant = new TenantContext();
        tenant.Resolve(accountId);
        await using var fence = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(factory.ConnectionString).Options,
            tenant, new FlockScope());
        await using var transaction = await fence.Database.BeginTransactionAsync();
        await fence.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM "SalesOrders" WHERE "Id" = {orderId} FOR UPDATE""");
        if (disableWorker)
            await fence.Database.ExecuteSqlInterpolatedAsync(
                $"""UPDATE "AspNetUsers" SET "DisabledAt" = CURRENT_TIMESTAMP WHERE "Id" = {workerId}""");
        else
            await fence.Database.ExecuteSqlInterpolatedAsync(
                $"""DELETE FROM "UserRoleAssignments" WHERE "UserId" = {workerId}""");
        var holderPid = await fence.BackendPidAsync();

        var confirm = worker.PostWithKeyAsync($"/api/v1/sales/{orderId}/confirm", Guid.NewGuid().ToString());
        Assert.True(await factory.WaitUntilDoneOrBlockedAsync(confirm, holderPid),
            "confirm must wait on the order lock before reading the role or assignments");

        await transaction.CommitAsync();
        var response = await confirm;

        Assert.Equal(disableWorker ? HttpStatusCode.Forbidden : HttpStatusCode.OK, response.StatusCode);
        var (quantityA, quantityB) = await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var lots = await db.EggLots.AsNoTracking().Where(l => l.Id == lotA || l.Id == lotB).ToListAsync();
            return (lots.Single(l => l.Id == lotA).QuantityAvailable, lots.Single(l => l.Id == lotB).QuantityAvailable);
        });
        Assert.Equal(disableWorker ? 5 : 0, quantityA);
        Assert.Equal(disableWorker ? 20 : 15, quantityB);
    }
}
