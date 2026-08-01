namespace Cluckwork.Api.Middleware;

// #307 — bounds for the database-coordinated claim/lease idempotency protocol.
// Both are seconds so ops can tune them via config without a redeploy.
public sealed class IdempotencyOptions
{
    public const string SectionName = "Idempotency";

    // A claim's lease: while held, no other request (in this process or
    // another replica) may execute the same key. Once the lease expires the
    // claim is presumed abandoned (the holder crashed, was killed, or the
    // pod died) and another request may STEAL it and re-execute — the
    // acceptance-criterion guarantee that a key can never be wedged forever.
    // Generous relative to expected handler latency: stealing while the
    // original is still genuinely alive costs wasted work (the loser's
    // mutation is rolled back — see IdempotencyMiddleware's guarded publish)
    // but stays correct; it is a performance trade-off, not a safety one.
    public int LeaseDurationSeconds { get; init; } = 30;

    // Bounds how long ANY SINGLE caller polls a live (unexpired) competing
    // claim before giving up with a definite 409 instead of hanging. This is
    // independent of the lease itself — the KEY still recovers via steal even
    // if every individual waiter times out first.
    public int MaxWaitSeconds { get; init; } = 30;
}
