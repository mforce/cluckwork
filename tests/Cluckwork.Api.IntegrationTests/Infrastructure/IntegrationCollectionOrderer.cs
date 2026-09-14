using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: TestCollectionOrderer(
    "Cluckwork.Api.IntegrationTests.Infrastructure.IntegrationCollectionOrderer",
    "Cluckwork.Api.IntegrationTests")]

namespace Cluckwork.Api.IntegrationTests.Infrastructure;

// #839: this collection contains most of the suite's tests and runs serially.
// Start it first so its work overlaps the smaller, independent collections.
public sealed class IntegrationCollectionOrderer : ITestCollectionOrderer
{
    public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections) =>
        new DefaultTestCollectionOrderer()
            .OrderTestCollections(testCollections)
            .OrderBy(collection => collection.DisplayName == IntegrationCollection.Name ? 0 : 1);
}
