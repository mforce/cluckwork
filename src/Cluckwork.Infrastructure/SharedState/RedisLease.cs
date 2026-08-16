namespace Cluckwork.Infrastructure.SharedState;

using StackExchange.Redis;

// #543 — Redis <see cref="ILease"/> (shared, multi-replica).
//
// Acquire is the native atomic SET-if-not-exists-with-expiry (value = the
// owner token); renew and release are compare-and-* Lua scripts, so a dead
// holder's lease is reclaimed by TTL and a previous holder can never release
// a lease that was re-granted to someone else. Synchronous by design: the
// port methods return <see cref="bool"/> and the callers (#545/#271
// single-runner guard) tolerate one short bounded Redis round trip.
internal sealed class RedisLease(IConnectionMultiplexer redis, string keyNamespace) : ILease
{
    private const string RenewScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
          return redis.call('PEXPIRE', KEYS[1], ARGV[2])
        else
          return 0
        end
        """;

    private const string ReleaseScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
          return redis.call('DEL', KEYS[1])
        else
          return 0
        end
        """;

    public bool TryAcquire(string key, string owner, TimeSpan ttl)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(owner);
        if (ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl));

        var db = redis.GetDatabase();
        return db.StringSet($"{keyNamespace}:{key}", owner, ttl, when: When.NotExists);
    }

    public bool Renew(string key, string owner, TimeSpan ttl)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(owner);
        if (ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl));

        var db = redis.GetDatabase();
        var result = db.ScriptEvaluate(
            RenewScript,
            new RedisKey[] { $"{keyNamespace}:{key}" },
            new RedisValue[] { owner, (long)ttl.TotalMilliseconds });
        return (long)result == 1;
    }

    public bool Release(string key, string owner)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(owner);

        var db = redis.GetDatabase();
        var result = db.ScriptEvaluate(
            ReleaseScript,
            new RedisKey[] { $"{keyNamespace}:{key}" },
            new RedisValue[] { owner });
        return (long)result == 1;
    }
}
