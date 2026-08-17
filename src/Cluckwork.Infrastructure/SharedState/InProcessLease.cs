namespace Cluckwork.Infrastructure.SharedState;

// #543 — in-process <see cref="ILease"/> fallback (Option B: a deliberately
// single-instance deploy runs without Redis).
//
// Thread-safe: registered as a process-wide singleton, so every
// read-check-write (the compare in compare-and-delete) is serialized under a
// single lock — the in-process stand-in for the atomicity a shared backend
// (Lua script) must provide. Entries expire lazily — an expired entry is
// dropped on the next access to its key, and a full sweep runs when the table
// grows past <see cref="_sweepThreshold"/>, so memory is bounded even when
// keys are never re-touched. All time comes from the injected
// <see cref="TimeProvider"/>.
internal sealed class InProcessLease(TimeProvider timeProvider) : ILease
{
    private sealed record Entry(string Owner, DateTimeOffset Expires);

    /// <summary>
    /// Number of live entries at which a full sweep of expired entries runs.
    /// Keeps the table bounded when callers mint keys they never revisit.
    /// </summary>
    private const int SweepThreshold = 4096;
    private const int SweepModulus = 256;

    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _leases = [];
    private int _sweepCount;

    public bool TryAcquire(string key, string owner, TimeSpan ttl)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(owner);
        if (ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl));

        lock (_lock)
        {
            // Clock read under the lock (see InProcessClaimOnceStore).
            var now = timeProvider.GetUtcNow();
            if (_leases.TryGetValue(key, out var entry) && now < entry.Expires)
                return false; // Live lease held by someone else.

            _leases[key] = new Entry(owner, now + ttl);
            MaybeSweep(now);
            return true;
        }
    }

    public bool Renew(string key, string owner, TimeSpan ttl)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(owner);
        if (ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl));

        lock (_lock)
        {
            var now = timeProvider.GetUtcNow();
            if (!_leases.TryGetValue(key, out var entry)
                || now >= entry.Expires
                || !entry.Owner.Equals(owner, StringComparison.Ordinal))
                return false;

            _leases[key] = new Entry(entry.Owner, now + ttl);
            return true;
        }
    }

    public bool Release(string key, string owner)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(owner);

        lock (_lock)
        {
            var now = timeProvider.GetUtcNow();
            // Compare-and-delete: only the current live holder may release.
            if (!_leases.TryGetValue(key, out var entry)
                || now >= entry.Expires
                || !entry.Owner.Equals(owner, StringComparison.Ordinal))
                return false;

            _leases.Remove(key);
            return true;
        }
    }

    private void MaybeSweep(DateTimeOffset now)
    {
        // Called with _lock held. Per-key lazy drops handle the common case;
        // the sweep bounds the table when many keys go quiet after their TTL.
        if (++_sweepCount % SweepModulus != 0 || _leases.Count <= SweepThreshold)
            return;

        // Collect-then-remove: removing from a Dictionary while enumerating it
        // is undefined behaviour (see InMemoryStepUpGrantRegistry.Prune).
        List<string>? expired = null;
        foreach (var (key, entry) in _leases)
            if (now >= entry.Expires)
                (expired ??= []).Add(key);

        if (expired is null) return;
        foreach (var key in expired)
            _leases.Remove(key);
    }
}
