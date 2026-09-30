namespace Cluckwork.Api.IntegrationTests.Infrastructure;

// #863 spike — each member keeps its OWN CluckworkWebApplicationFactory and its
// own migrated Postgres database (no ICollectionFixture here). This collection
// only serializes the members against each other; xUnit still runs it in
// parallel with every other collection. The full demo seed is CPU-bound on EF
// change detection (docs/plans/863-integration-collection-split/03-demo-seed-profile.md),
// so running these seeds one after another instead of overlapping is the
// hypothesis under test, not a shared-state requirement.
[CollectionDefinition(Name)]
public sealed class DemoSeedCollection
{
    public const string Name = "demo-seed";
}
