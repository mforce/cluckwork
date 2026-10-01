using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: TestCollectionOrderer(
    "Cluckwork.Api.IntegrationTests.Infrastructure.IntegrationCollectionOrderer",
    "Cluckwork.Api.IntegrationTests")]

namespace Cluckwork.Api.IntegrationTests.Infrastructure;

// #839: these two collections hold most of the suite's tests and each runs
// serially within itself. Start both first so their work overlaps the
// smaller, independent collections instead of running after them.
public sealed class IntegrationCollectionOrderer : ITestCollectionOrderer
{
    private static readonly HashSet<string> SharedHalves = [IntegrationCollectionA.Name, IntegrationCollectionB.Name];

    public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections) =>
        new DefaultTestCollectionOrderer()
            .OrderTestCollections(testCollections)
            .OrderBy(collection => SharedHalves.Contains(collection.DisplayName) ? 0 : 1);
}
