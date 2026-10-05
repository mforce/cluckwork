using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Catalog;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Modules.EggOperations.Eggs;
using Cluckwork.Domain.Modules.Finance.Expenses;
using Cluckwork.Domain.Modules.FlockManagement.Flocks;
using Cluckwork.Domain.Modules.GeneralInventory.Inventory;
using Cluckwork.Domain.Sales;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Cluckwork.Api.IntegrationTests;

public sealed class BusinessRecordModelTests
{
    private static readonly Type[] CreatedOnlyTypes =
    [
        typeof(UserRoleAssignment), typeof(BirdMovement), typeof(FeedUsage),
        typeof(InventoryMovement), typeof(EggInventoryMovement)
    ];

    private static readonly Type[] ChronologicalListTypes =
    [
        typeof(SalesOrder), typeof(SalesOrderItem), typeof(Expense), typeof(DailyEntry), typeof(EggLot),
        typeof(BirdMovement), typeof(Payment), typeof(InventoryLot), typeof(FeedUsage),
        typeof(WaterUsage), typeof(InventoryMovement), typeof(EggInventoryMovement)
    ];

    private static readonly (Type Type, string Reason)[] NotReadByTimeTypes =
    [
        (typeof(Account), "farm record resolved by id or farm code; operators list it by code"),
        (typeof(ApplicationUser), "user list is ordered by email"),
        (typeof(Customer), "customer list is ordered by name"),
        (typeof(DailyEntryGrade), "lines read through their daily entry; export orders by entry"),
        (typeof(EggGrade), "grades are ordered by sort order and name"),
        (typeof(EggUnitConversion), "conversions are ordered by unit code"),
        (typeof(ExpenseCategory), "categories are ordered by name"),
        (typeof(FarmLogo), "one logo row per farm"),
        (typeof(Flock), "flock screens are name-ordered; the export keeps PlacementDate, Id (#819)"),
        (typeof(InventoryItem), "items are ordered by name"),
        (typeof(Product), "products are ordered by name"),
        (typeof(ProductEggGradeMapping), "one mapping per product, read by product"),
        (typeof(SalesOrderAllocation), "allocations read through their sales order; export orders by order"),
        (typeof(UserRoleAssignment), "a user's role set, not a list")
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

        var timestampedTypes = db.Model.GetEntityTypes()
            .Where(entity => !entity.IsOwned()
                && typeof(ICreatedRecord).IsAssignableFrom(entity.ClrType))
            .Select(entity => entity.ClrType)
            .ToArray();

        foreach (var type in timestampedTypes)
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

        Assert.Equal(
            CreatedOnlyTypes.OrderBy(type => type.Name),
            timestampedTypes.Where(type => !typeof(IMutableRecord).IsAssignableFrom(type))
                .OrderBy(type => type.Name));
    }

    [Fact]
    public void Only_chronological_lists_have_a_unique_generated_sequence()
    {
        using var db = BuildContext();

        var timestampedTypes = db.Model.GetEntityTypes()
            .Where(entity => !entity.IsOwned()
                && typeof(ICreatedRecord).IsAssignableFrom(entity.ClrType))
            .Select(entity => entity.ClrType);

        foreach (var type in timestampedTypes)
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

    [Fact]
    public void Every_timestamped_record_is_declared_chronological_or_not_read_by_time()
    {
        using var db = BuildContext();

        var ownedTimestamped = db.Model.GetEntityTypes()
            .Where(entity => entity.IsOwned() && typeof(ICreatedRecord).IsAssignableFrom(entity.ClrType))
            .Select(entity => entity.ClrType.FullName).Distinct().Order().ToArray();
        Assert.True(ownedTimestamped.Length == 0,
            "Owned entity types cannot be timestamped business records: "
            + string.Join(", ", ownedTimestamped)
            + ". BusinessRecordModel configures no timestamps or Sequence for owned types; map the record as an entity type.");

        var timestampedTypes = db.Model.GetEntityTypes()
            .Where(entity => !entity.IsOwned()
                && typeof(ICreatedRecord).IsAssignableFrom(entity.ClrType))
            .Select(entity => entity.ClrType)
            .ToHashSet();
        var declaredTypes = ChronologicalListTypes
            .Concat(NotReadByTimeTypes.Select(entry => entry.Type))
            .ToArray();

        var undeclared = timestampedTypes.Except(declaredTypes).Select(type => type.FullName).Order().ToArray();
        Assert.True(undeclared.Length == 0,
            "Timestamped records missing from both ChronologicalListTypes and NotReadByTimeTypes: "
            + string.Join(", ", undeclared)
            + ". A record users page or read by time joins its module's chronological contribution and ChronologicalListTypes;"
            + " any other record joins NotReadByTimeTypes with its reason.");

        var declaredTwice = declaredTypes.GroupBy(type => type).Where(group => group.Count() > 1)
            .Select(group => group.Key.Name).Order().ToArray();
        Assert.True(declaredTwice.Length == 0, "Declared more than once: " + string.Join(", ", declaredTwice));

        var stale = declaredTypes.Except(timestampedTypes).Select(type => type.Name).Order().ToArray();
        Assert.True(stale.Length == 0, "Declared but not a mapped timestamped record: " + string.Join(", ", stale));
    }
}
