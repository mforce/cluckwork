namespace Cluckwork.Api.IntegrationTests.SharedState;

using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Infrastructure.RateLimiting;
using Cluckwork.Infrastructure.SharedState;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

// #545 — Program must use the same bound SharedStateOptions namespace for both
// the auth counter and report cap. Each backing store has its own key shape, so
// checking one only proves half the wiring.
public sealed class SharedStateNamespaceWiringTests(RedisFixture redis) : IClassFixture<RedisFixture>
{
    private const string KeyNamespace = "shared-state-namespace-wiring";

    [Fact]
    public async Task AuthCounterAndReportCap_UseTheConfiguredSharedStateNamespace()
    {
        await using var factory = new NamespaceFactory(redis.ConnectionString);
        await factory.InitializeAsync();

        var counter = factory.Services.GetRequiredService<IFixedWindowCounter>();
        await counter.IncrementAsync("auth-login:namespace-wiring", TimeSpan.FromMinutes(1));

        var accountId = Guid.NewGuid();
        var cap = factory.Services.GetRequiredService<DistributedReportConcurrencyLimiter>();
        await using var permit = await cap.AcquireAsync(accountId);
        Assert.NotNull(permit);

        var keys = new List<string>();
        foreach (var endpoint in redis.Redis.GetEndPoints())
        {
            var server = redis.Redis.GetServer(endpoint);
            await foreach (var key in server.KeysAsync(pattern: $"*{KeyNamespace}*"))
                keys.Add(key.ToString());
        }

        Assert.Contains(keys, key => key.StartsWith(
            $"{{{KeyNamespace}:win:auth-login:namespace-wiring}}:", StringComparison.Ordinal));
        Assert.Contains($"{KeyNamespace}:lease:report-cc:{accountId:N}:0", keys);
    }

    private sealed class NamespaceFactory(string redisConnection) : CluckworkWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("SharedState:Redis:ConnectionString", redisConnection);
            builder.UseSetting("SharedState:Redis:KeyNamespace", KeyNamespace);
        }
    }
}
