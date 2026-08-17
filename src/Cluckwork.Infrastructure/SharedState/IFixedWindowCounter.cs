namespace Cluckwork.Infrastructure.SharedState;

// #543 — atomic increment within a fixed time window (port for the auth
// limiters, #544, and the report concurrency cap, #545).
//
// The window for a key is the half-open interval
// [floor(now / window), floor(now / window) + window). The count accumulates
// for the duration of the current window and resets to zero when the window
// rolls over. Time comes exclusively from the injected <see cref="TimeProvider"/>
// — implementations must never read a wall clock directly, so tests can drive
// the window rollover with a fake clock.
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
    long Increment(string key, TimeSpan window);
}
