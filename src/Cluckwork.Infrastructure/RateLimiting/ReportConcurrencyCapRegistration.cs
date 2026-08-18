namespace Cluckwork.Infrastructure.RateLimiting;

using Cluckwork.Infrastructure.SharedState;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

// #545 — registers the distributed report-concurrency cap. Factory-deferred so
// IConnectionMultiplexer/TimeProvider resolve on first use (after Build),
// independent of registration order (mirrors SharedStateRegistration). A
// configured Redis => pinned Redis primary + in-process fallback; blank
// connection string => no IConnectionMultiplexer registered => in-process only
// (single-instance, Option B). The cap builds the concrete leases itself because
// pinning needs both backends, which the deleted per-call ILease decorator could
// not provide.
public static class ReportConcurrencyCapRegistration
{
    // TTL comfortably exceeds the longest bounded report plus one missed renewal;
    // renew well inside it. Renewal keeps a live report's slot; the TTL only
    // governs how fast a CRASHED holder's slot is reclaimed.
    private static readonly TimeSpan LeaseTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RenewInterval = TimeSpan.FromSeconds(20);

    public static void AddCluckworkReportConcurrencyCap(
        this IServiceCollection services, int permitLimit, string keyNamespace)
    {
        services.AddSingleton(sp =>
        {
            var mux = sp.GetService<IConnectionMultiplexer>();
            var clock = sp.GetRequiredService<TimeProvider>();
            ILease? redis = mux is null ? null : new RedisLease(mux, keyNamespace);
            ILease fallback = new InProcessLease(clock);
            return new DistributedReportConcurrencyLimiter(
                redis, fallback, clock,
                sp.GetRequiredService<ILogger<DistributedReportConcurrencyLimiter>>(),
                permitLimit, LeaseTtl, RenewInterval);
        });
    }
}
