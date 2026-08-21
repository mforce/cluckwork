namespace Cluckwork.Api.IntegrationTests.Infrastructure;

using Microsoft.EntityFrameworkCore.Metadata;

internal static class ReferenceDataComparison
{
    public static void AssertMappedPropertiesEqualByKey<TEntity, TKey>(
        IEntityType entityType,
        IEnumerable<TEntity> actualRows,
        IEnumerable<TEntity> expectedRows,
        Func<TEntity, TKey> keySelector,
        IReadOnlySet<string> excludedProperties)
        where TKey : notnull
    {
        var actual = ToUniqueDictionary(actualRows, keySelector, "actual");
        var expected = ToUniqueDictionary(expectedRows, keySelector, "expected");

        Assert.Equal(expected.Keys.OrderBy(key => key), actual.Keys.OrderBy(key => key));

        var properties = entityType.GetProperties()
            .Where(property => !excludedProperties.Contains(property.Name))
            .ToArray();

        foreach (var key in expected.Keys)
        {
            foreach (var property in properties)
            {
                Assert.NotNull(property.PropertyInfo);
                var propertyInfo = property.PropertyInfo!;
                Assert.Equal(
                    propertyInfo.GetValue(expected[key]),
                    propertyInfo.GetValue(actual[key]));
            }
        }
    }

    public static IReadOnlySet<string> AssertExactMappedPropertyPartition(
        IEntityType entityType,
        IReadOnlySet<string> comparedProperties,
        IReadOnlySet<string> excludedProperties)
    {
        Assert.Empty(comparedProperties.Intersect(excludedProperties));

        var mappedProperties = entityType.GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        var classifiedProperties = comparedProperties
            .Concat(excludedProperties)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(mappedProperties.Order(), classifiedProperties.Order());
        return excludedProperties;
    }

    private static Dictionary<TKey, TEntity> ToUniqueDictionary<TEntity, TKey>(
        IEnumerable<TEntity> rows,
        Func<TEntity, TKey> keySelector,
        string side)
        where TKey : notnull
    {
        var result = new Dictionary<TKey, TEntity>();
        foreach (var row in rows)
        {
            var key = keySelector(row);
            Assert.True(result.TryAdd(key, row), $"Duplicate {side} reference-data key '{key}'.");
        }

        return result;
    }
}
