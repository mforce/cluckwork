namespace Cluckwork.Infrastructure.SharedState;

using Cluckwork.Application.Common;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

// #543 — resilient decorator over <see cref="IClaimOnceStore"/> (grant
// replay, #338). FAILS CLOSED: when Redis throws, the claim is DENIED.
// Grant replay gates privileged operations (create Owner, reset Owner
// password, promote to Owner); when single-use cannot be proven, refusing is
// correct. This deliberately does NOT fall back to an in-process store — a
// per-process fallback would make the grant usable once per replica.
internal sealed class ResilientClaimOnceStore(
    IClaimOnceStore redis,
    ILogger<ResilientClaimOnceStore> logger) : IClaimOnceStore
{
    public bool TryClaim(string key, TimeSpan ttl)
    {
        try
        {
            return redis.TryClaim(key, ttl);
        }
        // RedisTimeoutException derives from TimeoutException, NOT RedisException,
        // so `catch (RedisException)` alone misses the commonest transient Redis
        // failure (a saturated pool / slow server) — the claim would then THROW
        // instead of failing closed. RedisCommandException is deliberately NOT
        // caught: a malformed command is our bug and must surface loudly, not be
        // masked as "Redis unavailable".
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            logger.LogWarning("{SecurityEvent} capability={Capability}",
                SecurityEvents.SharedStateRedisUnavailable, "grant-replay");
            return false; // fail closed: cannot prove single-use, so deny.
        }
    }
}
