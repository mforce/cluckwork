namespace Cluckwork.Api.IntegrationTests.SharedState;

using Testcontainers.Redis;

// #543 — real Redis (Testcontainers) for the shared-state contract suites.
//
// The Redis impls honour REDIS's server clock, not the injected
// TimeProvider — so these tests use real (short) TTLs and real waits, no
// FakeTimeProvider. Each test class uses its own keyNamespace (a fresh
// GUID) so no two tests can collide on a key.
public sealed class RedisFixture : IAsyncLifetime
{
    // Testcontainers.Redis 4.14: the parameterless RedisBuilder() is
    // obsolete (CS0618, build-breaking here) — pass the image explicitly.
    private readonly Testcontainers.Redis.RedisContainer _container =
        new RedisBuilder("redis:7.4-alpine@sha256:e7723ff73d963f5cc6d9c4643ea3d989527a402a319239054e9472a7fb9219a2").Build();

    public StackExchange.Redis.IConnectionMultiplexer Redis { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Redis = await StackExchange.Redis.ConnectionMultiplexer.ConnectAsync(_container.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        Redis?.Dispose();
        await _container.DisposeAsync();
    }
}
