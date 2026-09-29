namespace Cluckwork.Infrastructure.Persistence;

using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

internal static class BusinessRecordModel
{
    private static readonly BusinessRecordContribution[] Contributions =
    [
        CommerceBusinessRecords.Contribution,
        EggOperationsBusinessRecords.Contribution,
        FinanceBusinessRecords.Contribution,
        FlockManagementBusinessRecords.Contribution,
        GeneralInventoryBusinessRecords.Contribution,
        PlatformBusinessRecords.Contribution
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
        var chronologicalListContributions = Contributions
            .SelectMany(contribution => contribution.ChronologicalListTypes)
            .ToArray();
        var chronologicalLists = chronologicalListContributions.ToHashSet();
        var exclusions = Contributions
            .SelectMany(contribution => contribution.MappedExclusions)
            .ToHashSet();

        ValidateCensus(
            mappedTypes,
            timestampedRecords,
            Contributions,
            chronologicalListContributions,
            chronologicalLists,
            exclusions);

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
        BusinessRecordContribution[] contributions,
        Type[] chronologicalListContributions,
        HashSet<Type> chronologicalLists,
        HashSet<Type> exclusions)
    {
        if (chronologicalListContributions.Length != chronologicalLists.Count)
            throw new InvalidOperationException(DuplicateContributionMessage(
                contributions,
                contribution => contribution.ChronologicalListTypes,
                "chronological-list"));
        var exclusionContributions = contributions
            .SelectMany(contribution => contribution.MappedExclusions)
            .ToArray();
        if (exclusionContributions.Length != exclusions.Count)
            throw new InvalidOperationException(DuplicateContributionMessage(
                contributions,
                contribution => contribution.MappedExclusions,
                "mapped-exclusion"));
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
                $"Mapped entities have no business-record timestamp policy: {string.Join(", ", unclassifiedTypes.Select(type => type.FullName))}.");
    }

    private static string DuplicateContributionMessage(
        BusinessRecordContribution[] contributions,
        Func<BusinessRecordContribution, Type[]> selectTypes,
        string contributionKind)
    {
        var duplicateType = contributions
            .SelectMany(selectTypes)
            .GroupBy(type => type)
            .First(group => group.Count() > 1)
            .Key;
        var modules = contributions
            .Where(contribution => selectTypes(contribution).Contains(duplicateType))
            .Select(contribution => contribution.Module);
        return $"The {contributionKind} census contains duplicate contributions for '{duplicateType.Name}' from modules: {string.Join(", ", modules)}.";
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

internal sealed record BusinessRecordContribution(
    string Module,
    Type[] ChronologicalListTypes,
    Type[] MappedExclusions);
