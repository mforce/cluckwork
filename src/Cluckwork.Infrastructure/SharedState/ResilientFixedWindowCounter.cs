namespace Cluckwork.Infrastructure.SharedState;

using Cluckwork.Application.Common;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

// #543 — resilient decorator over <see cref="IFixedWindowCounter"/> (auth
// limiter, #544). FALLS BACK to the in-process implementation when Redis
// throws, and alarms. Falling back to a per-process partition means N
// replicas allow roughly N times the intended budget — bounded, and the
// dummy-PBKDF2 cost still applies to unknown users, so fail-open (no limit
// at all) is strictly worse. The alarm is load-bearing: a limiter silently
// stuck in fallback for weeks is the worst of both.
internal sealed class ResilientFixedWindowCounter(
    IFixedWindowCounter redis,
    IFixedWindowCounter fallback,
    ILogger<ResilientFixedWindowCounter> logger) : IFixedWindowCounter
{
    public long Increment(string key, TimeSpan window)
    {
        try
        {
            return redis.Increment(key, window);
        }
        // RedisTimeoutException derives from TimeoutException, not RedisException
        // — without it a slow/saturated Redis would THROW instead of falling
        // back, the exact silent-degradation the alarm exists to surface.
        // RedisCommandException is not caught on purpose (a bad command is our
        // bug, not a Redis outage).
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            logger.LogWarning("{SecurityEvent} capability={Capability}",
                SecurityEvents.SharedStateRedisUnavailable, "auth-rate-limit");
            return fallback.Increment(key, window);
        }
    }
}
