namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Catalog;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Eggs;
using Cluckwork.Domain.Expenses;
using Cluckwork.Domain.Flocks;
using Cluckwork.Domain.Inventory;
using Cluckwork.Domain.Sales;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

public sealed class BusinessRecordModelTests
{
    private static readonly Type[] TimestampedTypes =
    [
        typeof(Account), typeof(FarmLogo), typeof(UserRoleAssignment),
        typeof(EggUnitConversion), typeof(Product), typeof(ProductEggGradeMapping),
        typeof(DailyEntry), typeof(DailyEntryGrade), typeof(EggGrade), typeof(EggLot),
        typeof(EggInventoryMovement), typeof(Expense), typeof(ExpenseCategory),
        typeof(Flock), typeof(BirdMovement), typeof(FeedUsage), typeof(InventoryItem),
        typeof(InventoryLot), typeof(InventoryMovement), typeof(WaterUsage),
        typeof(Customer), typeof(Payment), typeof(SalesOrder), typeof(SalesOrderItem),
        typeof(SalesOrderAllocation), typeof(ApplicationUser)
    ];

    private static readonly Type[] CreatedOnlyTypes =
    [
        typeof(UserRoleAssignment), typeof(BirdMovement), typeof(FeedUsage),
        typeof(InventoryMovement), typeof(EggInventoryMovement)
    ];

    private static readonly Type[] ChronologicalListTypes =
    [
        typeof(SalesOrder), typeof(Expense), typeof(DailyEntry), typeof(EggLot),
        typeof(BirdMovement), typeof(Payment), typeof(InventoryLot), typeof(FeedUsage),
        typeof(WaterUsage), typeof(InventoryMovement), typeof(EggInventoryMovement)
    ];

    private static AppDbContext BuildContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only;Username=none;Password=none")
            .Options;
        return new AppDbContext(options, new TenantContext(), new FlockScope());
    }

    [Fact]
    public void Business_records_have_the_declared_timestamp_shape()
    {
        using var db = BuildContext();

        Assert.Equal(26, TimestampedTypes.Distinct().Count());
        Assert.Equal(21, TimestampedTypes.Except(CreatedOnlyTypes).Count());

        foreach (var type in TimestampedTypes)
        {
            var entity = db.Model.FindEntityType(type);
            Assert.NotNull(entity);
            Assert.True(typeof(ICreatedRecord).IsAssignableFrom(type), $"{type.Name} is missing ICreatedRecord");

            var createdAt = entity.FindProperty(nameof(ICreatedRecord.CreatedAtUtc));
            Assert.NotNull(createdAt);
            Assert.Equal(typeof(DateTimeOffset), createdAt.ClrType);
            Assert.False(createdAt.IsNullable);

            var mutable = !CreatedOnlyTypes.Contains(type);
            Assert.Equal(mutable, typeof(IMutableRecord).IsAssignableFrom(type));
            Assert.Equal(mutable, entity.FindProperty(nameof(IMutableRecord.UpdatedAtUtc)) is not null);
        }
    }

    [Fact]
    public void Only_chronological_lists_have_a_unique_generated_sequence()
    {
        using var db = BuildContext();

        foreach (var type in TimestampedTypes)
        {
            var entity = db.Model.FindEntityType(type)!;
            var sequence = entity.FindProperty("Sequence");
            var chronological = ChronologicalListTypes.Contains(type);

            Assert.Equal(chronological, sequence is not null);
            if (!chronological) continue;

            Assert.Equal(typeof(long), sequence!.ClrType);
            Assert.Equal(ValueGenerated.OnAdd, sequence.ValueGenerated);
            Assert.Contains(entity.GetIndexes(), index =>
                index.IsUnique && index.Properties.Count == 1 && index.Properties[0] == sequence);
        }
    }
}
