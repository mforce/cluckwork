namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Features.Sales;
using Cluckwork.Domain.Eggs;
using Cluckwork.Domain.Expenses;
using Cluckwork.Domain.Flocks;
using Cluckwork.Domain.Sales;
using Cluckwork.Infrastructure.Repositories;

// #819 — these lists lead with a date-only value. Their second key must carry
// insertion chronology; a random v4 Guid is deterministic but not chronological.
[Collection(IntegrationCollection.Name)]
public sealed class ListChronologyTests(CluckworkWebApplicationFactory factory)
{
    // Deliberately oppose Guid-descending order: the earlier row has the larger
    // id, so every pre-#819 query returns it first and this test goes red.
    private static readonly Guid EarlierId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
    private static readonly Guid LaterId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateOnly Today = new(2026, 9, 13);

    [Fact]
    public async Task SameDayLists_ReturnTheLaterInsertedRowFirst()
    {
        var accountId = await factory.SeedAccountWithUserAsync(
            $"chronology-{Guid.NewGuid():N}@test.local");

        await factory.WithTenantScopeAsync(accountId, async db =>
        {
            var farmId = Guid.NewGuid();
            var flockId = Guid.NewGuid();
            var customer = Customer.Create(Guid.NewGuid(), accountId, "Chronology customer", "1");
            var category = ExpenseCategory.Create(
                Guid.NewGuid(), accountId, farmId, "Chronology category");
            var grade = EggGrade.Create(
                Guid.NewGuid(), accountId, farmId, "Chronology grade",
                EggGradeType.Size, 0, isSaleable: true);
            var flock = Flock.Create(
                flockId, accountId, farmId, Guid.NewGuid(), "Chronology flock",
                "Test breed", Today.AddDays(-30), 100);
            db.AddRange(customer, category, grade, flock);
            await db.SaveChangesAsync();

            await InsertSeparatelyAsync(
                SalesOrder.Create(EarlierId, accountId, customer.Id, "SO-EARLIER", Today, "USD"),
                SalesOrder.Create(LaterId, accountId, customer.Id, "SO-LATER", Today, "USD"));
            await InsertSeparatelyAsync(
                BirdMovement.Create(EarlierId, accountId, flockId, Today, BirdMovementType.Cull, 1),
                BirdMovement.Create(LaterId, accountId, flockId, Today, BirdMovementType.Cull, 2));
            await InsertSeparatelyAsync(
                Expense.Create(EarlierId, accountId, farmId, category.Id, Today,
                    "Earlier expense", 100, "USD", 2),
                Expense.Create(LaterId, accountId, farmId, category.Id, Today,
                    "Later expense", 200, "USD", 2));
            await InsertSeparatelyAsync(
                EggLot.Create(EarlierId, accountId, flockId, Today, grade.Id, 10),
                EggLot.Create(LaterId, accountId, flockId, Today, grade.Id, 20));
            await InsertSeparatelyAsync(
                DailyEntry.Create(EarlierId, accountId, farmId, Guid.NewGuid(), flockId, Today),
                DailyEntry.Create(LaterId, accountId, farmId, Guid.NewGuid(), flockId, Today));

            var orderFilter = new SalesOrderListFilter(null, null, null, null, SettlementScope.Hidden);
            var hiddenOrders = await new SalesOrderRepository(db).ListAsync(orderFilter, 10, 0);
            AssertLaterFirst(hiddenOrders.Select(x => x.Order.Id));

            orderFilter = orderFilter with { Settlement = SettlementScope.Visible };
            var visibleOrders = await new SalesOrderRepository(db).ListAsync(orderFilter, 10, 0);
            AssertLaterFirst(visibleOrders.Select(x => x.Order.Id));

            AssertLaterFirst((await new BirdMovementRepository(db)
                .ListByFlockAsync(flockId, 10, 0)).Select(x => x.Id));
            AssertLaterFirst((await new ExpenseRepository(db)
                .ListAsync(null, null, null, 10, 0)).Select(x => x.Id));
            AssertLaterFirst((await new EggLotRepository(db)
                .ListAsync(null, null, null, 10, 0)).Select(x => x.Id));
            AssertLaterFirst((await new DailyEntryRepository(db)
                .ListAsync(null, null, null, 10, 0)).Select(x => x.Id));

            async Task InsertSeparatelyAsync<TEntity>(TEntity earlier, TEntity later)
                where TEntity : class
            {
                db.Add(earlier);
                await db.SaveChangesAsync();
                db.Add(later);
                await db.SaveChangesAsync();
            }
        });
    }

    private static void AssertLaterFirst(IEnumerable<Guid> ids) =>
        Assert.Equal([LaterId, EarlierId], ids.ToArray());
}
