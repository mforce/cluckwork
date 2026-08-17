namespace Cluckwork.Infrastructure.SharedState;

using StackExchange.Redis;

// #543 — Redis <see cref="IFixedWindowCounter"/> (shared, multi-replica).
//
// Server-time-bucketed fixed window: the bucket is derived from Redis's OWN
// clock (TIME inside the script), so every replica agrees on the window
// regardless of host clock skew. The INCR + conditional PEXPIRE run in ONE
// Lua script — never a separate INCR then EXPIRE, which would leave a key
// with no expiry in the gap. The PEXPIRE only fires when the counter is
// created (c == 1), so a hot window never rewrites the TTL.
//
// SINGLE-NODE Redis only: the script derives the real key (KEYS[1] .. bucket)
// from the server clock, so the accessed key cannot be declared in KEYS. Redis
// Cluster rejects an undeclared-key access (CROSSSLOT), so on a clustered Redis
// every Increment would error and the resilient decorator would silently run on
// the in-process fallback for good. The scale-out target is one shared
// single-node Redis, so this is a documented deployment constraint, not a bug.
internal sealed class RedisFixedWindowCounter(IConnectionMultiplexer redis, string keyNamespace) : IFixedWindowCounter
{
    private const string IncrementScript = """
        local t = redis.call('TIME')
        local ms = (tonumber(t[1]) * 1000) + math.floor(tonumber(t[2]) / 1000)
        local windowMs = tonumber(ARGV[1])
        local bucket = math.floor(ms / windowMs)
        local k = KEYS[1] .. ':' .. bucket
        local c = redis.call('INCR', k)
        if c == 1 then
          redis.call('PEXPIRE', k, windowMs)
        end
        return c
        """;

    public long Increment(string key, TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(key);
        // Minimum window is 1ms (matches InProcessFixedWindowCounter): a
        // sub-millisecond window rounds windowMs to 0, which the Lua would
        // divide by.
        if (window < TimeSpan.FromMilliseconds(1))
            throw new ArgumentOutOfRangeException(nameof(window), "window must be at least 1 millisecond.");

        var db = redis.GetDatabase();
        var result = db.ScriptEvaluate(
            IncrementScript,
            new RedisKey[] { $"{keyNamespace}:win:{key}" },
            new RedisValue[] { (long)window.TotalMilliseconds });
        return (long)result;
    }
}
