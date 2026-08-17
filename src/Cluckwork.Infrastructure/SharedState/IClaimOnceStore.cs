namespace Cluckwork.Infrastructure.SharedState;

// #543 — single-use claim with TTL (port for grant replay, #338).
//
// A key can be claimed exactly once per TTL window. The first caller wins;
// every other caller is refused while the claim is live, and the key becomes
// claimable again once the TTL has elapsed. Time comes exclusively from the
// injected <see cref="TimeProvider"/> — implementations must never read a
// wall clock directly, so tests can drive expiry with a fake clock.
internal interface IClaimOnceStore
{
    /// <summary>
    /// Attempts to claim <paramref name="key"/> for the duration
    /// <paramref name="ttl"/>.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> the first time the key is claimed and again once
    /// the previous claim's TTL has elapsed; <see langword="false"/> while an
    /// unexpired claim on the key exists.
    /// </returns>
    bool TryClaim(string key, TimeSpan ttl);
}
