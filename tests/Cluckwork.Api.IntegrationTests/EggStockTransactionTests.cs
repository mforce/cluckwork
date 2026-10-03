using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Application.Features.EggLots;
using Cluckwork.Domain.Eggs;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests;

// #854: IEggStock's FOR UPDATE locks end with the transaction that took them,
// so the port refuses to run outside one and a reservation refuses to plan or
// draw once that transaction is no longer the current one.
[Collection(IntegrationCollection.Name)]
public sealed class EggStockTransactionTests(CluckworkWebApplicationFactory factory)
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

    [Fact]
    public async Task Reservation_AfterItsTransactionCommits_RefusesToPlanOrDraw()
    {
        var (accountId, gradeId, lotId) = await SeedLotAsync();
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        services.GetRequiredService<TenantContext>().Resolve(accountId);
        var unitOfWork = services.GetRequiredService<IUnitOfWork>();
        var reservation = await LockAsync(services, unitOfWork, accountId, gradeId);
        Assert.Null(services.GetRequiredService<AppDbContext>().Database.CurrentTransaction);

        AssertRefused(Assert.Throws<InvalidOperationException>(() =>
            reservation.Plan([new SaleDemandLine(Guid.NewGuid(), gradeId, 10)])));
        AssertRefused(await Assert.ThrowsAsync<InvalidOperationException>(() => reservation.DrawAsync(
            new PlannedEggLotDraw(Guid.NewGuid(), lotId, 10), "SalesOrderAllocation", Guid.NewGuid(), default)));
        await unitOfWork.SaveChangesAsync();

        Assert.Equal((30, 0), await StockAsync(accountId, lotId));
    }

    [Fact]
    public async Task Reservation_InsideALaterTransaction_RefusesToPlanOrDraw()
    {
        var (accountId, gradeId, lotId) = await SeedLotAsync();
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        services.GetRequiredService<TenantContext>().Resolve(accountId);
        var unitOfWork = services.GetRequiredService<IUnitOfWork>();
        var reservation = await LockAsync(services, unitOfWork, accountId, gradeId);

        AssertRefused(await Assert.ThrowsAsync<InvalidOperationException>(() =>
            unitOfWork.ExecuteInTransactionAsync(_ =>
            {
                reservation.Plan([new SaleDemandLine(Guid.NewGuid(), gradeId, 10)]);
                return Task.FromResult(true);
            })));
        AssertRefused(await Assert.ThrowsAsync<InvalidOperationException>(() =>
            unitOfWork.ExecuteInTransactionAsync(async ct =>
            {
                await reservation.DrawAsync(
                    new PlannedEggLotDraw(Guid.NewGuid(), lotId, 10), "SalesOrderAllocation", Guid.NewGuid(), ct);
                return true;
            })));

        Assert.Equal((30, 0), await StockAsync(accountId, lotId));
    }

    [Fact]
    public async Task LockAndRestore_OutsideATransaction_Refuse()
    {
        var (accountId, gradeId, lotId) = await SeedLotAsync();
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        services.GetRequiredService<TenantContext>().Resolve(accountId);
        var stock = services.GetRequiredService<IEggStock>();

        AssertRefused(await Assert.ThrowsAsync<InvalidOperationException>(() =>
            stock.LockForSaleAsync(accountId, [gradeId], Today, default)));
        AssertRefused(await Assert.ThrowsAsync<InvalidOperationException>(() => stock.RestoreAsync(
            accountId, new Dictionary<Guid, int> { [lotId] = 1 }, "SalesOrder", Guid.NewGuid(), "late", default)));
        await services.GetRequiredService<IUnitOfWork>().SaveChangesAsync();

        Assert.Equal((30, 0), await StockAsync(accountId, lotId));
    }

    private static void AssertRefused(InvalidOperationException thrown) =>
        Assert.Contains("transaction", thrown.Message, StringComparison.Ordinal);

    private static async Task<IEggStockReservation> LockAsync(
        IServiceProvider services, IUnitOfWork unitOfWork, Guid accountId, Guid gradeId)
    {
        IEggStockReservation? reservation = null;
        await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            reservation = await services.GetRequiredService<IEggStock>()
                .LockForSaleAsync(accountId, [gradeId], Today, ct);
            return true;
        });
        return reservation!;
    }

    private async Task<(Guid AccountId, Guid GradeId, Guid LotId)> SeedLotAsync()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"u-{Guid.NewGuid():N}@test.local");
        var gradeId = (await factory.SeedEggGradesAsync(accountId, Guid.NewGuid(), "Large"))["Large"];
        return (accountId, gradeId, await factory.SeedEggLotAsync(accountId, gradeId, 30, productionDate: Today));
    }

    // Availability and Sale movements, read through a fresh context.
    private Task<(int Available, int SaleMovements)> StockAsync(Guid accountId, Guid lotId) =>
        factory.WithTenantScopeAsync(accountId, async db => (
            await db.EggLots.Where(l => l.Id == lotId).Select(l => l.QuantityAvailable).SingleAsync(),
            await db.EggInventoryMovements.CountAsync(m => m.EggLotId == lotId && m.MovementType == EggMovementType.Sale)));
}
