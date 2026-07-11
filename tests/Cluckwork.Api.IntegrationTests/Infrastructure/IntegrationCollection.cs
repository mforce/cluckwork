namespace Cluckwork.Api.IntegrationTests.Infrastructure;

// Shares one Postgres container + WebApplicationFactory across every test class in the
// collection, so the container spins up once per test run rather than per class.
[CollectionDefinition(Name)]
public sealed class IntegrationCollection : ICollectionFixture<CluckworkWebApplicationFactory>
{
    public const string Name = "integration";
}
