namespace Cluckwork.Infrastructure.Persistence;

using Cluckwork.Domain.Auditing;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Eggs;
using Cluckwork.Domain.Expenses;
using Cluckwork.Domain.Flocks;
using Cluckwork.Domain.Inventory;
using Cluckwork.Domain.Sales;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Jobs;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal static class BusinessRecordModel
{
    // Sequence is persistence-only, so this is the deliberate policy list;
    // unlike timestamp classification, no domain interface should expose it.
    private static readonly Type[] ChronologicalListTypes =
    [
        typeof(SalesOrder),
        typeof(Expense),
        typeof(DailyEntry),
        typeof(EggLot),
        typeof(BirdMovement),
        typeof(Payment),
        typeof(InventoryLot),
        typeof(FeedUsage),
        typeof(WaterUsage),
        typeof(InventoryMovement),
        typeof(EggInventoryMovement)
    ];

    private static readonly Type[] MappedExclusions =
    [
        typeof(AuditEvent),
        typeof(ApplicationRole),
        typeof(IdentityRoleClaim<Guid>),
        typeof(IdentityUserClaim<Guid>),
        typeof(IdentityUserLogin<Guid>),
        typeof(IdentityUserRole<Guid>),
        typeof(IdentityUserToken<Guid>),
        typeof(IdentityPasskeyData),
        typeof(RefreshToken),
        typeof(IdempotencyRecord),
        typeof(SimulationSeedState),
        typeof(DurableJob)
    ];

    public static void Apply(ModelBuilder builder)
    {
        var mappedTypes = builder.Model.GetEntityTypes()
            .Where(entityType => !entityType.IsOwned())
            .Select(entityType => entityType.ClrType)
            .ToHashSet();
        var timestampedRecords = mappedTypes
            .Where(typeof(ICreatedRecord).IsAssignableFrom)
            .ToHashSet();
        var chronologicalLists = ChronologicalListTypes.ToHashSet();
        var exclusions = MappedExclusions.ToHashSet();

        ValidateCensus(mappedTypes, timestampedRecords, chronologicalLists, exclusions);

        foreach (var recordType in timestampedRecords)
        {
            var entityType = builder.Model.FindEntityType(recordType)!;
            ConfigureTimestamps(builder.Entity(recordType), entityType, recordType);
        }

        foreach (var listType in chronologicalLists)
        {
            var entity = builder.Entity(listType);
            var sequence = entity.Property<long>("Sequence")
                .ValueGeneratedOnAdd()
                .UseIdentityAlwaysColumn();
            sequence.Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
            sequence.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.HasIndex("Sequence").IsUnique();
        }
    }

    private static void ValidateCensus(
        HashSet<Type> mappedTypes,
        HashSet<Type> timestampedRecords,
        HashSet<Type> chronologicalLists,
        HashSet<Type> exclusions)
    {
        if (ChronologicalListTypes.Length != chronologicalLists.Count)
            throw new InvalidOperationException("The chronological-list census contains a duplicate type.");
        if (!chronologicalLists.IsSubsetOf(timestampedRecords))
            throw new InvalidOperationException("Every chronological list must be a timestamped business record.");
        if (timestampedRecords.Overlaps(exclusions))
            throw new InvalidOperationException("A mapped entity cannot be both timestamped and excluded.");

        foreach (var excludedType in exclusions)
        {
            if (!mappedTypes.Contains(excludedType))
                throw new InvalidOperationException($"Mapped exclusion '{excludedType.Name}' is not mapped.");
        }

        var unclassifiedTypes = mappedTypes
            .Except(timestampedRecords)
            .Except(exclusions)
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToArray();
        if (unclassifiedTypes.Length != 0)
            throw new InvalidOperationException(
                $"Mapped entities have no business-record timestamp policy: {string.Join(", ", unclassifiedTypes.Select(type => type.Name))}.");
    }

    private static void ConfigureTimestamps(
        EntityTypeBuilder entity,
        IReadOnlyEntityType entityType,
        Type recordType)
    {
        var createdAt = entityType.FindProperty(nameof(ICreatedRecord.CreatedAtUtc));
        ValidateTimestampProperty(createdAt, recordType, nameof(ICreatedRecord.CreatedAtUtc));
        ConfigureCreatedTimestamp(entity.Property(nameof(ICreatedRecord.CreatedAtUtc)));

        if (typeof(IMutableRecord).IsAssignableFrom(recordType))
        {
            var updatedAt = entityType.FindProperty(nameof(IMutableRecord.UpdatedAtUtc));
            ValidateTimestampProperty(updatedAt, recordType, nameof(IMutableRecord.UpdatedAtUtc));
            ConfigureUpdatedTimestamp(entity.Property(nameof(IMutableRecord.UpdatedAtUtc)));
            return;
        }

        if (entityType.FindProperty(nameof(IMutableRecord.UpdatedAtUtc)) is not null)
            throw new InvalidOperationException($"Created-only business record '{recordType.Name}' maps {nameof(IMutableRecord.UpdatedAtUtc)}.");
    }

    private static void ValidateTimestampProperty(IReadOnlyProperty? property, Type recordType, string propertyName)
    {
        if (property is null || property.ClrType != typeof(DateTimeOffset) || property.IsNullable)
            throw new InvalidOperationException($"Business record '{recordType.Name}' must map a non-null {nameof(DateTimeOffset)} {propertyName}.");
    }

    private static void ConfigureCreatedTimestamp(PropertyBuilder property)
    {
        property.ValueGeneratedOnAdd();
        property.Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        property.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
    }

    private static void ConfigureUpdatedTimestamp(PropertyBuilder property)
    {
        property.ValueGeneratedOnAddOrUpdate();
        property.Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        property.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
    }
}
