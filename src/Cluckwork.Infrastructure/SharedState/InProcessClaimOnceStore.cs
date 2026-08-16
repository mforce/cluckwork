namespace Cluckwork.Infrastructure.SharedState;

// #543 — in-process <see cref="IClaimOnceStore"/> fallback (Option B: a
// deliberately single-instance deploy runs without Redis).
//
// Thread-safe: registered as a process-wide singleton, so every mutation is
// serialized under a single lock. Entries expire lazily — an expired entry is
// dropped on the next access to its key, and a full sweep runs when the table
// grows past <see cref="_sweepThreshold"/>, so memory is bounded even when
// keys are never re-touched. All time comes from the injected
// <see cref="TimeProvider"/>.
internal sealed class InProcessClaimOnceStore(TimeProvider timeProvider) : IClaimOnceStore
{
    /// <summary>
    /// Number of live entries at which a full sweep of expired entries runs.
    /// Keeps the table bounded when callers mint keys they never revisit.
    /// </summary>
    private const int SweepThreshold = 4096;
    private const int SweepModulus = 256;

    private readonly object _lock = new();
    private readonly Dictionary<string, DateTimeOffset> _claims = [];
    private int _sweepCount;

    public bool TryClaim(string key, TimeSpan ttl)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl));

        var now = timeProvider.GetUtcNow();
        lock (_lock)
        {
            if (_claims.TryGetValue(key, out var expires))
            {
                if (now < expires)
                    return false;

                // TTL elapsed — drop the stale entry and re-claim below.
                _claims[key] = now + ttl;
            }
            else
            {
                _claims[key] = now + ttl;
            }

            MaybeSweep(now);
            return true;
        }
    }

    private void MaybeSweep(DateTimeOffset now)
    {
        // Called with _lock held. The per-key lazy drop above handles the
        // common case; the sweep only bounds the table when many keys go
        // quiet after their TTL.
        if (++_sweepCount % SweepModulus != 0 || _claims.Count <= SweepThreshold)
            return;

        // Collect-then-remove: removing from a Dictionary while enumerating it
        // is undefined behaviour (see InMemoryStepUpGrantRegistry.Prune).
        List<string>? expired = null;
        foreach (var (key, expires) in _claims)
            if (now >= expires)
                (expired ??= []).Add(key);

        if (expired is null) return;
        foreach (var key in expired)
            _claims.Remove(key);
    }
}
