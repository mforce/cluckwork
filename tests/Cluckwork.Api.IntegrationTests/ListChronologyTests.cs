namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Features.Export;
using Cluckwork.Application.Features.Sales;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Eggs;
using Cluckwork.Domain.Expenses;
using Cluckwork.Domain.Flocks;
using Cluckwork.Domain.Inventory;
using Cluckwork.Domain.Sales;
using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.Repositories;

// #819 — these lists lead with a business date and row-creation time. Sequence
// completes the insertion order without relying on a random v4 Guid.
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
            var item = InventoryItem.Create(
                Guid.NewGuid(), accountId, farmId, "Chronology feed",
                InventoryCategory.Feed, "kg", Money.Zero("USD"));
            db.AddRange(customer, category, grade, flock, item);
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
            await InsertSeparatelyAsync(
                Payment.Create(EarlierId, accountId, EarlierId, customer.Id, Today,
                    100, "USD", 2, PaymentMethod.Cash),
                Payment.Create(LaterId, accountId, EarlierId, customer.Id, Today,
                    200, "USD", 2, PaymentMethod.Cash));
            await InsertSeparatelyAsync(
                InventoryLot.Create(EarlierId, accountId, item.Id, Today, 10,
                    Money.Zero("USD"), null, null),
                InventoryLot.Create(LaterId, accountId, item.Id, Today, 20,
                    Money.Zero("USD"), null, null));
            await InsertSeparatelyAsync(
                FeedUsage.Create(EarlierId, accountId, flockId, item.Id, Today, 1,
                    "kg", Money.Zero("USD")),
                FeedUsage.Create(LaterId, accountId, flockId, item.Id, Today, 2,
                    "kg", Money.Zero("USD")));
            await InsertSeparatelyAsync(
                WaterUsage.Create(EarlierId, accountId, flockId, Today, 1,
                    "L", WaterSource.Well, null, null),
                WaterUsage.Create(LaterId, accountId, flockId, Today, 2,
                    "L", WaterSource.Well, null, null));

            var earlierInventoryMovement = InventoryMovement.Create(
                accountId, item.Id, null, Today, InventoryMovementType.Adjustment, 1, "kg");
            var laterInventoryMovement = InventoryMovement.Create(
                accountId, item.Id, null, Today, InventoryMovementType.Adjustment, 2, "kg");
            await InsertSeparatelyAsync(earlierInventoryMovement, laterInventoryMovement);

            var earlierEggMovement = EggInventoryMovement.Create(
                EarlierId, accountId, EarlierId, EggMovementType.Production, 1,
                nameof(DailyEntry), EarlierId);
            var laterEggMovement = EggInventoryMovement.Create(
                LaterId, accountId, EarlierId, EggMovementType.Production, 2,
                nameof(DailyEntry), LaterId);
            await InsertSeparatelyAsync(earlierEggMovement, laterEggMovement);

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
            Assert.Equal(
                [EarlierId, LaterId],
                (await new PaymentRepository(db)
                    .ListByOrderAsync(EarlierId)).Select(x => x.Id).ToArray());
            AssertLaterFirst((await new InventoryLotRepository(db)
                .ListByItemAsync(item.Id)).Select(x => x.Id));
            AssertLaterFirst((await new FeedUsageRepository(db)
                .ListAsync(null, null, null, 10, 0)).Select(x => x.Id));
            AssertLaterFirst((await new WaterUsageRepository(db)
                .ListAsync(null, null, null, 10, 0)).Select(x => x.Id));
            Assert.Equal(
                [laterInventoryMovement.Id, earlierInventoryMovement.Id],
                (await new InventoryMovementRepository(db)
                    .ListByItemAsync(item.Id, 10, 0)).Select(x => x.Id).ToArray());
            AssertLaterFirst((await new EggInventoryMovementRepository(db)
                .ListByLotAsync(EarlierId)).Select(x => x.Id));

            var exports = new ExportQueries(db, new TenantContext(), new FlockScope());
            await AssertExportOrderAsync("sales-orders", EarlierId, LaterId);
            await AssertExportOrderAsync("bird-movements", EarlierId, LaterId);
            await AssertExportOrderAsync("expenses", EarlierId, LaterId);
            await AssertExportOrderAsync("egg-lots", EarlierId, LaterId);
            await AssertExportOrderAsync("daily-entries", EarlierId, LaterId);
            await AssertExportOrderAsync("payments", EarlierId, LaterId);
            await AssertExportOrderAsync("inventory-lots", EarlierId, LaterId);
            await AssertExportOrderAsync("feed-usages", EarlierId, LaterId);
            await AssertExportOrderAsync("water-usages", EarlierId, LaterId);
            await AssertExportOrderAsync(
                "inventory-movements", earlierInventoryMovement.Id, laterInventoryMovement.Id);
            await AssertExportOrderAsync("egg-inventory-movements", EarlierId, LaterId);

            async Task InsertSeparatelyAsync<TEntity>(TEntity earlier, TEntity later)
                where TEntity : class
            {
                db.Add(earlier);
                await db.SaveChangesAsync();
                db.Add(later);
                await db.SaveChangesAsync();
            }

            async Task AssertExportOrderAsync(string datasetName, Guid earlier, Guid later)
            {
                var dataset = Assert.IsType<ExportDataset>(exports.GetDataset(datasetName));
                var ids = new List<Guid>();
                await foreach (var row in dataset.Rows)
                {
                    var id = Assert.IsType<Guid>(row[0]);
                    if (id == earlier || id == later)
                        ids.Add(id);
                }

                Assert.Equal([earlier, later], ids);
            }
        });
    }

    private static void AssertLaterFirst(IEnumerable<Guid> ids) =>
        Assert.Equal([LaterId, EarlierId], ids.ToArray());
}
