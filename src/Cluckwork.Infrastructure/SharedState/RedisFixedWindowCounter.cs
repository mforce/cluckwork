namespace Cluckwork.Infrastructure.SharedState;

using System.Threading;
using System.Threading.Tasks;
using StackExchange.Redis;

// #543 — Redis <see cref="IFixedWindowCounter"/> (shared, multi-replica).
//
// Server-time-bucketed fixed window: the bucket is derived from Redis's OWN
// clock (TIME inside the script), so every replica agrees on the window
// regardless of host clock skew. The INCR + conditional PEXPIRE run in ONE
// Lua script — never a separate INCR then EXPIRE, which would leave a key
// with no expiry in the gap. The PEXPIRE only fires when the counter is
// created (c == 1), so a hot window never rewrites the TTL. The script also
// returns the key's remaining PTTL so IncrementAsync can hand back a
// Retry-After computed on Redis's clock, not the API host's (#544 review).
//
// Cluster-safe: the script derives the real key (KEYS[1] .. bucket) from the
// server clock, so the accessed key cannot be declared in KEYS. To keep it in
// the same hash slot as the declared KEYS[1], the key wraps the routing part in
// a hash tag ({namespace:win:key}), so KEYS[1] and KEYS[1]:bucket hash to the
// same slot and Redis Cluster accepts the script. On single-node Redis the
// braces are ordinary key characters with no effect.
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
        local pttl = redis.call('PTTL', k)
        return {c, pttl}
        """;

    public long Increment(string key, TimeSpan window)
    {
        ValidateWindow(key, window);
        var db = redis.GetDatabase();
        var result = (RedisResult[])db.ScriptEvaluate(
            IncrementScript,
            new RedisKey[] { RealKey(key) },
            new RedisValue[] { (long)window.TotalMilliseconds })!;
        return (long)result[0];
    }

    public async ValueTask<FixedWindowResult> IncrementAsync(
        string key, TimeSpan window, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateWindow(key, window);
        var db = redis.GetDatabase();
        // StackExchange.Redis commands take no CancellationToken (it multiplexes and
        // cannot cancel an in-flight command); the token is honoured before dispatch only.
        var result = (RedisResult[])(await db.ScriptEvaluateAsync(
            IncrementScript,
            new RedisKey[] { RealKey(key) },
            new RedisValue[] { (long)window.TotalMilliseconds }))!;
        var count = (long)result[0];
        var pttlMs = (long)result[1];
        // PTTL is -1 (no expiry) / -2 (no key) only in states this script never leaves a key
        // in (it PEXPIREs on create); fall back to the full window if Redis ever reports one.
        var remainingMs = pttlMs < 0 ? (long)window.TotalMilliseconds : pttlMs;
        return new FixedWindowResult(count, TimeSpan.FromMilliseconds(remainingMs));
    }

    private RedisKey RealKey(string key) => $"{{{keyNamespace}:win:{key}}}";

    private static void ValidateWindow(string key, TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(key);
        // Whole milliseconds only (>= 1ms), matching InProcessFixedWindowCounter:
        // windowMs truncates any sub-ms remainder, so a non-whole-ms window would
        // bucket on a narrower width than its nominal length. Reject it rather
        // than silently truncate.
        if (window.Ticks < TimeSpan.TicksPerMillisecond
            || window.Ticks % TimeSpan.TicksPerMillisecond != 0)
            throw new ArgumentOutOfRangeException(nameof(window),
                "window must be a whole number of milliseconds, at least 1.");
    }
}
