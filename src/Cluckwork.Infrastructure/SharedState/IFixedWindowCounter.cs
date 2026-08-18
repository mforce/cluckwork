namespace Cluckwork.Infrastructure.SharedState;

using System.Threading;
using System.Threading.Tasks;

// #543 — atomic increment within a fixed time window (port for the auth
// limiters, #544, and the report concurrency cap, #545).
//
// The window for a key is the half-open interval
// [floor(now / window), floor(now / window) + window). The count accumulates
// for the duration of the current window and resets to zero when the window
// rolls over. Time comes exclusively from the injected <see cref="TimeProvider"/>
// (in-process) or Redis's own server clock (Redis) — implementations must never
// read a wall clock directly, so tests can drive the window rollover with a fake
// clock.
internal interface IFixedWindowCounter
{
    /// <summary>
    /// Atomically increments the counter for <paramref name="key"/> within the
    /// current <paramref name="window"/>.
    /// </summary>
    /// <returns>
    /// The count for <paramref name="key"/> after the increment, within the
    /// current window. Counts never carry over across a window boundary.
    /// </returns>
    /// <remarks>
    /// Synchronous — blocks the caller on the backing store's round trip. On a
    /// request hot path prefer <see cref="IncrementAsync"/>; this overload stays
    /// for the rate limiter's synchronous acquire path (not the ASP.NET middleware
    /// hot path) and for callers already off the request thread.
    /// </remarks>
    long Increment(string key, TimeSpan window);

    /// <summary>
    /// Non-blocking <see cref="Increment"/>, returning the post-increment count
    /// AND the time remaining until the current window rolls over. Remaining comes
    /// from the SAME clock that owns the window (Redis server time for the Redis
    /// implementation, the injected <see cref="TimeProvider"/> in-process), so a
    /// Retry-After built from it never drifts against the API host clock (#544 review).
    /// </summary>
    ValueTask<FixedWindowResult> IncrementAsync(
        string key, TimeSpan window, CancellationToken cancellationToken = default);
}

// #544 — the outcome of one fixed-window increment.
internal readonly record struct FixedWindowResult(long Count, TimeSpan Remaining);
