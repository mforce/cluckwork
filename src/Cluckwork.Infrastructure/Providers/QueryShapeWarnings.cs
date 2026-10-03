using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Cluckwork.Infrastructure.Providers;

public static class QueryShapeWarnings
{
    public static bool Enabled { get; } = string.Equals(
        Environment.GetEnvironmentVariable("Database__ThrowQueryShapeWarnings"),
        "true", StringComparison.OrdinalIgnoreCase);

    public static void Configure(WarningsConfigurationBuilder warnings) => warnings.Throw(
        CoreEventId.PossibleUnintendedCollectionNavigationNullComparisonWarning,
        CoreEventId.PossibleUnintendedReferenceComparisonWarning,
        CoreEventId.RowLimitingOperationWithoutOrderByWarning,
        CoreEventId.FirstWithoutOrderByAndFilterWarning,
        CoreEventId.DistinctAfterOrderByWithoutRowLimitingOperatorWarning,
        CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning,
        RelationalEventId.QueryPossibleUnintendedUseOfEqualsWarning,
        RelationalEventId.MultipleCollectionIncludeWarning);
}
