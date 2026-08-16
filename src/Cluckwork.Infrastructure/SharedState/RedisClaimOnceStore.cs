namespace Cluckwork.Infrastructure.SharedState;

using StackExchange.Redis;

// #543 — Redis <see cref="IClaimOnceStore"/> (shared, multi-replica).
//
// Single-use claim via the native atomic SET-if-not-exists-with-expiry: Redis
// performs the "absent? set + expire" in one step, so there is no gap in which
// a key could exist without an expiry. Expiry is Redis's own — no client
// clock is involved. Synchronous by design: the port methods return
// <see cref="bool"/>, and the callers (#338 grant replay) are on the request
// path where a bounded, short Redis round trip is acceptable.
internal sealed class RedisClaimOnceStore(IConnectionMultiplexer redis, string keyNamespace) : IClaimOnceStore
{
    public bool TryClaim(string key, TimeSpan ttl)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl));

        var db = redis.GetDatabase();
        return db.StringSet($"{keyNamespace}:{key}", "1", ttl, when: When.NotExists);
    }
}
