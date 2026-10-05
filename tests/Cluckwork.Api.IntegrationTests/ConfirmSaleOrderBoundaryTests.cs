using Cluckwork.Domain.Modules.EggOperations.Contracts;
using Cluckwork.Domain.Modules.EggOperations.Eggs;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests;

// Both live authorization reads must follow the order lock as well as the account lock.
[Collection(IntegrationCollection.Name)]
public sealed class ConfirmSaleOrderBoundaryTests(CluckworkWebApplicationFactory factory)
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

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
        var farmId = Guid.NewGuid();
        var gradeId = (await factory.SeedEggGradesAsync(accountId, farmId, "Large"))["Large"];
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
        var commands = new ConfirmCommands();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(commands))));
        var worker = host.CreateClient(TestHarness.Cookieless(factory));
        worker.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await factory.LoginForAccessTokenAsync(workerEmail));

        var orderId = await factory.SeedSalesOrderAsync(accountId, gradeId, 10);

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

        Assert.Equal(disableWorker
            ? ["account lock", "order lock", "active actor"]
            : new[] { "account lock", "order lock", "active actor", "effective role", "assignments", "stock lock" },
            commands.Sequence.ToArray());
        Assert.Equal(disableWorker ? HttpStatusCode.Forbidden : HttpStatusCode.OK, response.StatusCode);
        var (quantityA, quantityB) = await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var lots = await db.EggLots.AsNoTracking().Where(l => l.Id == lotA || l.Id == lotB).ToListAsync();
            return (lots.Single(l => l.Id == lotA).QuantityAvailable, lots.Single(l => l.Id == lotB).QuantityAvailable);
        });
        Assert.Equal(disableWorker ? 5 : 0, quantityA);
        Assert.Equal(disableWorker ? 20 : 15, quantityB);
    }

    private sealed class ConfirmCommands : DbCommandInterceptor
    {
        public ConcurrentQueue<string> Sequence { get; } = new();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.Transaction is null) return ValueTask.FromResult(result);
            var sql = command.CommandText;
            var step = sql switch
            {
                _ when sql.Contains("FOR SHARE", StringComparison.Ordinal) => "account lock",
                _ when sql.Contains("\"SalesOrders\"", StringComparison.Ordinal)
                    && sql.Contains("FOR UPDATE", StringComparison.Ordinal) => "order lock",
                _ when sql.Contains("FROM \"AspNetUsers\"", StringComparison.Ordinal) => "active actor",
                _ when sql.Contains("FROM \"AspNetUserRoles\"", StringComparison.Ordinal) => "effective role",
                _ when sql.Contains("FROM \"UserRoleAssignments\"", StringComparison.Ordinal) => "assignments",
                _ when sql.Contains("\"EggLots\"", StringComparison.Ordinal)
                    && sql.Contains("FOR UPDATE", StringComparison.Ordinal) => "stock lock",
                _ => null,
            };
            if (step is not null) Sequence.Enqueue(step);
            return ValueTask.FromResult(result);
        }
    }
}
