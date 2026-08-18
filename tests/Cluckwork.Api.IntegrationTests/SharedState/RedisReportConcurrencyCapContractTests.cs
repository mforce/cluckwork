namespace Cluckwork.Api.IntegrationTests.SharedState;

using Cluckwork.Infrastructure.RateLimiting;
using Cluckwork.Infrastructure.SharedState;
using Microsoft.Extensions.Logging.Abstractions;

// #545 — the two-instance guarantee against a REAL shared store (Testcontainers
// Redis, mirrors RedisLeaseContractTests): two limiter instances sharing one
// Redis enforce ONE combined per-account permit count, not two. The
// deterministic in-process form is ReportConcurrencyCapContractTests.
public sealed class RedisReportConcurrencyCapContractTests(RedisFixture fixture)
    : IClassFixture<RedisFixture>
{
    [Fact]
    public async Task Two_instances_sharing_one_redis_enforce_one_combined_count()
    {
        var store = new RedisLease(fixture.Redis, Guid.NewGuid().ToString("N"));
        var account = Guid.NewGuid();
        var a = NewLimiter(store);
        var b = NewLimiter(store);

        await using var p1 = await a.AcquireAsync(account);
        await using var p2 = await a.AcquireAsync(account);
        await using var p3 = await b.AcquireAsync(account);

        Assert.NotNull(p1);
        Assert.NotNull(p2);
        Assert.Null(p3); // combined count is 2 across both instances, not 4
    }

    private static DistributedReportConcurrencyLimiter NewLimiter(ILease store) =>
        new(store, new InProcessLease(TimeProvider.System), TimeProvider.System,
            NullLogger<DistributedReportConcurrencyLimiter>.Instance,
            permitLimit: 2, ttl: TimeSpan.FromSeconds(30), renewInterval: TimeSpan.FromHours(1));
}
