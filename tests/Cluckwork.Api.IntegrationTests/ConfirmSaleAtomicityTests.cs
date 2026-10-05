using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Commerce.Contracts;
using Cluckwork.Application.Modules.Commerce.Sales;
using Cluckwork.Application.Modules.Commerce.Sales.ConfirmSale;
using Cluckwork.Application.Modules.EggOperations.EggLots;
using Cluckwork.Application.Modules.EggOperations.Eggs;
using Cluckwork.Domain.Modules.Commerce.Contracts;
using Cluckwork.Domain.Modules.Commerce.Sales;
using Cluckwork.Domain.Modules.EggOperations.Eggs;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cluckwork.Api.IntegrationTests;

// #854: a confirm commits its lot draws, Sale movements, allocation rows, order
// state and audit row together or not at all. Each case faults one step of the
// confirm on the second of two draws and finds nothing changed. The handler
// runs outside HTTP, so IdempotencyMiddleware's request transaction does not
// wrap it. Each fault first checks the two rules that make the confirm atomic,
// so breaking either one alone fails here: the confirm is inside a transaction,
// and no draw was saved before the end.
[Collection(IntegrationCollection.Name)]
public sealed class ConfirmSaleAtomicityTests(CluckworkWebApplicationFactory factory)
{
    public enum FaultPoint { AfterLotAllocation, AfterEggMovement, AfterAllocationRows, AfterOrderConfirmation }

    private sealed record Snapshot(
        string Lots, int Movements, int Allocations, SalesOrderStatus Status, int OrderVersion, int AuditRows);

    [Theory]
    [InlineData(FaultPoint.AfterLotAllocation)]
    [InlineData(FaultPoint.AfterEggMovement)]
    [InlineData(FaultPoint.AfterAllocationRows)]
    [InlineData(FaultPoint.AfterOrderConfirmation)]
    public async Task Confirm_FailureMidTransaction_PersistsNothing(FaultPoint fault)
    {
        var email = $"u-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var userId = await factory.WithTenantScopeAsync(accountId, db =>
            db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync());
        var grades = await factory.SeedEggGradesAsync(accountId, Guid.NewGuid(), "Large");
        var older = await factory.SeedEggLotAsync(accountId, grades["Large"], 30, productionDate: Today.AddDays(-5));
        var newer = await factory.SeedEggLotAsync(accountId, grades["Large"], 40, productionDate: Today.AddDays(-2));
        var orderId = await factory.SeedSalesOrderAsync(accountId, grades["Large"], 50);
        var before = await SnapshotAsync(accountId, orderId, [older, newer]);

        using (var scope = factory.Services.CreateScope().ResolveTenantAndActor(accountId, userId, email))
        {
            var services = scope.ServiceProvider;
            var db = services.GetRequiredService<AppDbContext>();
            var stock = new EggStock(services.GetRequiredService<IEggLotRepository>(),
                new FaultingMovements(services.GetRequiredService<IEggInventoryMovementRepository>(), fault, db),
                services.GetRequiredService<ICurrentTransaction>());
            var handler = ActivatorUtilities.CreateInstance<ConfirmSaleHandler>(services, stock,
                new FaultingAllocations(services.GetRequiredService<ISalesOrderAllocationRepository>(), fault, db),
                new FaultingAuditWriter(services.GetRequiredService<IAuditWriter>(), fault, db));
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(
                new ConfirmSaleCommand(orderId), accountId, userId, CancellationToken.None));
            Assert.Equal(FaultMessage(fault), thrown.Message);
        }

        // Each seeded lot carries its Production movement; adding the line bumped the order once.
        Assert.Equal(new Snapshot($"{older}:30:0,{newer}:40:0", 2, 0, SalesOrderStatus.Draft, 1, 0), before);
        Assert.Equal(before, await SnapshotAsync(accountId, orderId, [older, newer]));
    }

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

    private Task<Snapshot> SnapshotAsync(Guid accountId, Guid orderId, Guid[] lotIds) =>
        factory.WithTenantScopeAsync(accountId, async db =>
        {
            var lots = new List<string>();
            foreach (var id in lotIds)
            {
                var lot = await db.EggLots.AsNoTracking().SingleAsync(l => l.Id == id);
                lots.Add($"{lot.Id}:{lot.QuantityAvailable}:{lot.Version}");
            }
            var order = await db.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
            return new Snapshot(
                string.Join(",", lots),
                await db.EggInventoryMovements.CountAsync(m => lotIds.Contains(m.EggLotId)),
                await db.SalesOrderAllocations.CountAsync(a => a.SalesOrderId == orderId),
                order.Status, order.Version,
                await db.AuditEvents.CountAsync(e => e.EntityId == orderId));
        });

    private static string FaultMessage(FaultPoint at) => $"#854 atomicity guard: faulting {at}.";

    // Both lots are drawn, so an Unchanged lot at the fault is a draw already saved.
    private static Exception Fault(FaultPoint at, AppDbContext db) =>
        db.Database.CurrentTransaction is null
            ? new Xunit.Sdk.XunitException($"{at}: the confirm runs outside a transaction")
            : db.ChangeTracker.Entries<EggLot>().Any(e => e.State == EntityState.Unchanged)
                ? new Xunit.Sdk.XunitException($"{at}: a drawn lot was already saved before the fault")
                : new InvalidOperationException(FaultMessage(at));

    private sealed class FaultingMovements(IEggInventoryMovementRepository inner, FaultPoint fault, AppDbContext db)
        : IEggInventoryMovementRepository
    {
        private int _calls;

        public async Task AddAsync(EggInventoryMovement movement, CancellationToken ct = default)
        {
            var second = ++_calls == 2;
            if (second && fault == FaultPoint.AfterLotAllocation) throw Fault(fault, db);
            await inner.AddAsync(movement, ct);
            if (second && fault == FaultPoint.AfterEggMovement) throw Fault(fault, db);
        }

        public Task AddRangeAsync(IEnumerable<EggInventoryMovement> movements, CancellationToken ct = default) =>
            inner.AddRangeAsync(movements, ct);

        public Task<IReadOnlyList<EggInventoryMovement>> ListByLotAsync(Guid eggLotId, CancellationToken ct = default) =>
            inner.ListByLotAsync(eggLotId, ct);
    }

    private sealed class FaultingAllocations(ISalesOrderAllocationRepository inner, FaultPoint fault, AppDbContext db)
        : ISalesOrderAllocationRepository
    {
        public async Task AddRangeAsync(IReadOnlyList<SalesOrderAllocation> allocations, CancellationToken ct = default)
        {
            await inner.AddRangeAsync(allocations, ct);
            if (fault == FaultPoint.AfterAllocationRows) throw Fault(fault, db);
        }

        public Task<IReadOnlyList<SalesOrderAllocation>> ListPendingByOrderAsync(
            Guid salesOrderId, CancellationToken ct = default) =>
            inner.ListPendingByOrderAsync(salesOrderId, ct);
    }

    private sealed class FaultingAuditWriter(IAuditWriter inner, FaultPoint fault, AppDbContext db) : IAuditWriter
    {
        public Task WriteAsync(string action, string entityType, Guid entityId, string? reason = null,
            object? details = null, CancellationToken ct = default) =>
            fault == FaultPoint.AfterOrderConfirmation
                ? throw Fault(fault, db)
                : inner.WriteAsync(action, entityType, entityId, reason, details, ct);
    }
}
