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
internal sealed class RedisFixedWindowCounter(IConnectionMultiplexer redis, string keyNamespace) : IFixedWindowCounter
{
    private const string IncrementScript = """
        local t = redis.call('TIME')
        local ms = (t[1] * 1000) + math.floor(t[2] / 1000)
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
        if (window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window));

        var db = redis.GetDatabase();
        var result = db.ScriptEvaluate(
            IncrementScript,
            new RedisKey[] { $"{keyNamespace}:{key}" },
            new RedisValue[] { (long)window.TotalMilliseconds });
        return (long)result;
    }
}
