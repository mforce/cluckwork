namespace Cluckwork.Api.IntegrationTests.Infrastructure;

// Each half shares one Postgres container + WebApplicationFactory across its
// own members, so the container spins up once per half rather than per class.
// The two halves get separate factories and separate migrated databases, so
// xUnit runs them concurrently with each other instead of as one 94-class
// serial block. #863's 05-collection-split.md records why this split happened
// now (it didn't help before #1002 and #1003) and the correctness audit for
// which classes may move between halves and which must stay together.
[CollectionDefinition(Name)]
public sealed class IntegrationCollectionA : ICollectionFixture<CluckworkWebApplicationFactory>
{
    public const string Name = "integration-a";
}

[CollectionDefinition(Name)]
public sealed class IntegrationCollectionB : ICollectionFixture<CluckworkWebApplicationFactory>
{
    public const string Name = "integration-b";
}
