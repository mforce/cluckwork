namespace Cluckwork.Infrastructure.SharedState;

using Cluckwork.Application.Common;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

// #543 — resilient decorator over <see cref="ILease"/> (report concurrency
// cap, #545). FALLS BACK to the in-process implementation on every method
// when Redis throws, and alarms per fallen-back call. The lease bounds heavy
// report queries (a capacity protection, not an abuse mitigation) — an
// unbounded per-replica ceiling exhausts the DB pool for every farm, so a
// local per-instance ceiling is strictly better than no ceiling.
internal sealed class ResilientLease(
    ILease redis,
    ILease fallback,
    ILogger<ResilientLease> logger) : ILease
{
    public bool TryAcquire(string key, string owner, TimeSpan ttl)
    {
        try
        {
            return redis.TryAcquire(key, owner, ttl);
        }
        catch (RedisException)
        {
            logger.LogWarning("{SecurityEvent} capability={Capability}",
                SecurityEvents.SharedStateRedisUnavailable, "report-concurrency");
            return fallback.TryAcquire(key, owner, ttl);
        }
    }

    public bool Renew(string key, string owner, TimeSpan ttl)
    {
        try
        {
            return redis.Renew(key, owner, ttl);
        }
        catch (RedisException)
        {
            logger.LogWarning("{SecurityEvent} capability={Capability}",
                SecurityEvents.SharedStateRedisUnavailable, "report-concurrency");
            return fallback.Renew(key, owner, ttl);
        }
    }

    public bool Release(string key, string owner)
    {
        try
        {
            return redis.Release(key, owner);
        }
        catch (RedisException)
        {
            logger.LogWarning("{SecurityEvent} capability={Capability}",
                SecurityEvents.SharedStateRedisUnavailable, "report-concurrency");
            return fallback.Release(key, owner);
        }
    }
}
