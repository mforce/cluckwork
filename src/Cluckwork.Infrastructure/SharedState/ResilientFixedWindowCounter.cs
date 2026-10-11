using Cluckwork.Application.Common;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Cluckwork.Infrastructure.SharedState;

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
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            Alarm();
            return fallback.Increment(key, window);
        }
    }

    public async ValueTask<FixedWindowResult> IncrementAsync(
        string key, TimeSpan window, CancellationToken cancellationToken = default)
    {
        try
        {
            return await redis.IncrementAsync(key, window, cancellationToken);
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            Alarm();
            return await fallback.IncrementAsync(key, window, cancellationToken);
        }
    }

    // Also reads what the fallback counted for the same key and window: Redis can answer a
    // read while refusing increments, and the increments it refused landed here. The larger
    // of the two, not the sum: an increment Redis applied before its reply timed out landed
    // in both. That never double counts and undercounts by at most the smaller count.
    public async ValueTask<FixedWindowResult> CountAsync(
        string key, TimeSpan window, CancellationToken cancellationToken = default)
    {
        var local = await fallback.CountAsync(key, window, cancellationToken);
        try
        {
            var shared = await redis.CountAsync(key, window, cancellationToken);
            return shared with { Count = Math.Max(shared.Count, local.Count) };
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            Alarm();
            return local;
        }
    }

    // RedisTimeoutException derives from TimeoutException, not RedisException
    // — without it a slow/saturated Redis would THROW instead of falling
    // back, the exact silent-degradation the alarm exists to surface.
    // RedisCommandException is not caught on purpose (a bad command is our
    // bug, not a Redis outage).
    private void Alarm() =>
        logger.LogWarning("{SecurityEvent} capability={Capability}",
            SecurityEvents.SharedStateRedisUnavailable, "auth-rate-limit");
}
