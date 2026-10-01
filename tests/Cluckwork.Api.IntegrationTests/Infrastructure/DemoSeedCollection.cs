namespace Cluckwork.Api.IntegrationTests.Infrastructure;

// Serializes its members against each other only; xUnit still runs this
// collection in parallel with every other collection. Each member keeps its
// OWN CluckworkWebApplicationFactory and its own migrated Postgres database —
// never add an ICollectionFixture here, or members will share one database
// and reintroduce #1000's false pass.
//
// The full demo seed is CPU-bound on EF change detection, so classes that all
// run it were contending for the same CPU when left to run in parallel.
// Serializing them measured a ~6.9% faster local suite; see
// docs/plans/863-integration-collection-split/04-seed-serialization.md.
//
// Add a new class here only if it runs the FULL seed. A class that only
// reaches a seed's failure path (e.g. DemoSeedNoOwnerTests) doesn't contend
// the same way and doesn't belong in this collection.
[CollectionDefinition(Name)]
public sealed class DemoSeedCollection
{
    public const string Name = "demo-seed";
}
