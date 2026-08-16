namespace Cluckwork.Infrastructure.SharedState;


// #543 — in-process <see cref="IFixedWindowCounter"/> fallback (Option B: a
// deliberately single-instance deploy runs without Redis).
//
// Thread-safe: registered as a process-wide singleton, so the
// read-check-write on each entry is serialized under a single lock — the
// in-process stand-in for the atomicity a shared backend (Lua script) must
// provide. Entries are window-keyed and drop lazily when their window has
// rolled over; a full sweep runs when the table grows past
// <see cref="_sweepThreshold"/>, so memory is bounded even when keys are
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
    private readonly Dictionary<string, (DateTimeOffset WindowStart, long Count)> _counters = [];
    private int _sweepCount;

    public long Increment(string key, TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window));

        var now = timeProvider.GetUtcNow();
        var windowStart = WindowStart(now, window);
        lock (_lock)
        {
            if (_counters.TryGetValue(key, out var entry) && entry.WindowStart == windowStart)
            {
                _counters[key] = (entry.WindowStart, entry.Count + 1);
            }
            else
            {
                // First increment in this window (or a stale, rolled-over
                // entry) — the count starts fresh.
                _counters[key] = (windowStart, 1);
            }

            MaybeSweep(windowStart);
            return _counters[key].Count;
        }
    }

    private static DateTimeOffset WindowStart(DateTimeOffset now, TimeSpan window)
    {
        var windowSeconds = (long)window.TotalSeconds;
        // Floor division (toward negative infinity): the window for a key is
        // [floor(epoch / window), floor(epoch / window) + window), so the
        // rollover instant is wall-clock aligned, never per-key.
        var epochSeconds = now.ToUnixTimeSeconds();
        var remainder = epochSeconds % windowSeconds;
        var quotient = remainder >= 0 ? epochSeconds / windowSeconds : epochSeconds / windowSeconds - 1;
        return DateTimeOffset.FromUnixTimeSeconds(quotient * windowSeconds);
    }

    private void MaybeSweep(DateTimeOffset windowStart)
    {
        // Called with _lock held. Per-key lazy drops handle the common case;
        // the sweep bounds the table when many keys go quiet across windows.
        if (++_sweepCount % SweepModulus != 0 || _counters.Count <= SweepThreshold)
            return;

        // Collect-then-remove: removing from a Dictionary while enumerating it
        // is undefined behaviour (see InMemoryStepUpGrantRegistry.Prune).
        List<string>? expired = null;
        foreach (var (key, entry) in _counters)
            if (entry.WindowStart < windowStart)
                (expired ??= []).Add(key);

        if (expired is null) return;
        foreach (var key in expired)
            _counters.Remove(key);
    }
}
