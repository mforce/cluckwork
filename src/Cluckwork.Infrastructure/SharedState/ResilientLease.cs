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
//
// KNOWN DEGRADATION while Redis flaps: acquire/renew/release each pick a
// backend independently, so an acquire that landed in Redis then a renew that
// falls back in-process (or the reverse) can leave a lease "split" across the
// two stores — the effective cap becomes per-replica rather than global, the
// same looseness the counter accepts under fallback. That is tolerable HERE
// because this port bounds capacity, not correctness. A use that needs true
// cross-replica mutual exclusion (e.g. the #271 job single-runner) must NOT
// rely on this fallback: #271 uses a Postgres advisory lock, deliberately not
// this port. Do not repurpose this lease for job serialization without making
// acquire fail CLOSED on a Redis error.
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
        // RedisTimeoutException derives from TimeoutException, not RedisException;
        // catching only the latter would let a slow Redis throw instead of
        // falling back. RedisCommandException stays uncaught on purpose (our bug).
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
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
        // RedisTimeoutException derives from TimeoutException, not RedisException;
        // catching only the latter would let a slow Redis throw instead of
        // falling back. RedisCommandException stays uncaught on purpose (our bug).
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
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
        // RedisTimeoutException derives from TimeoutException, not RedisException;
        // catching only the latter would let a slow Redis throw instead of
        // falling back. RedisCommandException stays uncaught on purpose (our bug).
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            logger.LogWarning("{SecurityEvent} capability={Capability}",
                SecurityEvents.SharedStateRedisUnavailable, "report-concurrency");
            return fallback.Release(key, owner);
        }
    }
}
