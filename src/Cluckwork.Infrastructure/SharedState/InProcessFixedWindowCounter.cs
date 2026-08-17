namespace Cluckwork.Infrastructure.SharedState;


// #543 — in-process <see cref="IFixedWindowCounter"/> fallback (Option B: a
// deliberately single-instance deploy runs without Redis).
//
// Thread-safe: registered as a process-wide singleton, so the
// read-check-write on each entry is serialized under a single lock — the
// in-process stand-in for the atomicity a shared backend (Lua script) must
// provide. Entries are window-keyed and drop lazily when their window has
// rolled over; a full sweep runs when the table grows past
// <see cref="SweepThreshold"/>, so memory is bounded even when keys are
// never re-touched. All time comes from the injected
// <see cref="TimeProvider"/>.
internal sealed class InProcessFixedWindowCounter(TimeProvider timeProvider) : IFixedWindowCounter
{
    /// <summary>
    /// Number of live entries at which a full sweep of expired entries runs.
    /// Keeps the table bounded when callers mint keys they never revisit.
    /// </summary>
    private const int SweepThreshold = 4096;
    private const int SweepModulus = 256;

    private readonly object _lock = new();
    // Each entry stores its OWN window end (WindowStart + window). The sweep
    // drops an entry on its own expiry, never on the current caller's
    // windowStart — keys may carry different window lengths, and comparing a
    // 15-minute counter against a 5-minute caller's windowStart would evict it
    // while it is still live.
    private readonly Dictionary<string, (DateTimeOffset WindowStart, DateTimeOffset WindowEnd, long Count)> _counters = [];
    private int _sweepCount;

    public long Increment(string key, TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(key);
        // Minimum window is 1ms — matches RedisFixedWindowCounter. Bucketing on
        // a sub-millisecond window rounds the window length to 0, which the
        // Redis Lua would divide by and this code would modulo by.
        if (window < TimeSpan.FromMilliseconds(1))
            throw new ArgumentOutOfRangeException(nameof(window), "window must be at least 1 millisecond.");

        lock (_lock)
        {
            // Read the clock UNDER the lock. Computing the window outside it
            // lets a thread that stalls before acquiring the lock write a stale
            // (rolled-over) window over a newer one, resetting a live count.
            var now = timeProvider.GetUtcNow();
            var windowStart = WindowStart(now, window);
            if (_counters.TryGetValue(key, out var entry) && entry.WindowStart == windowStart)
            {
                _counters[key] = (entry.WindowStart, entry.WindowEnd, entry.Count + 1);
            }
            else
            {
                // First increment in this window (or a stale, rolled-over
                // entry) — the count starts fresh.
                _counters[key] = (windowStart, windowStart + window, 1);
            }

            MaybeSweep(now);
            return _counters[key].Count;
        }
    }

    private static DateTimeOffset WindowStart(DateTimeOffset now, TimeSpan window)
    {
        // Bucket on MILLISECONDS to match RedisFixedWindowCounter's Lua script
        // (which buckets on server TIME in ms): a sub-second window must behave
        // identically on both backends. (long)window.TotalSeconds was 0 for any
        // window under 1s — a modulo-by-zero crash in-process while Redis, which
        // buckets on ms, handled it fine.
        var windowMs = (long)window.TotalMilliseconds;
        // Floor division (toward negative infinity): the window for a key is
        // [floor(epoch / window) * window, + window), so the rollover instant is
        // wall-clock aligned, never per-key. epochMs is always positive for
        // post-1970 clocks; the negative branch is kept for correctness.
        var epochMs = now.ToUnixTimeMilliseconds();
        var quotient = epochMs >= 0 || epochMs % windowMs == 0
            ? epochMs / windowMs
            : epochMs / windowMs - 1;
        return DateTimeOffset.FromUnixTimeMilliseconds(quotient * windowMs);
    }

    private void MaybeSweep(DateTimeOffset now)
    {
        // Called with _lock held. Per-key lazy drops handle the common case;
        // the sweep bounds the table when many keys go quiet across windows.
        if (++_sweepCount % SweepModulus != 0 || _counters.Count <= SweepThreshold)
            return;

        // Collect-then-remove: removing from a Dictionary while enumerating it
        // is undefined behaviour (see InMemoryStepUpGrantRegistry.Prune). Drop
        // on each entry's OWN window end, not a shared windowStart.
        List<string>? expired = null;
        foreach (var (key, entry) in _counters)
            if (entry.WindowEnd <= now)
                (expired ??= []).Add(key);

        if (expired is null) return;
        foreach (var key in expired)
            _counters.Remove(key);
    }
}
