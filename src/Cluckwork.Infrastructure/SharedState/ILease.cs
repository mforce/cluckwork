namespace Cluckwork.Infrastructure.SharedState;

// #543 — owned, renewable lease with compare-and-delete release (port for the
// single-runner guarantee, #545/#271).
//
// A lease is held by exactly one <c>owner</c> token at a time, for a TTL. When
// the TTL elapses without renewal the lease is free again and may be acquired
// by anyone — a dead holder's lease is reclaimed by expiry, never by the
// holder itself. Release is compare-and-delete: only the current holder may
// release; an owner whose lease already expired and was re-acquired by someone
// else must NOT release the new holder's lease. Time comes exclusively from
// the injected <see cref="TimeProvider"/> — implementations must never read a
// wall clock directly, so tests can drive expiry with a fake clock.
internal interface ILease
{
    /// <summary>
    /// Attempts to acquire the lease on <paramref name="key"/> for
    /// <paramref name="ttl"/>.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if the lease was free (unheld, or held by someone
    /// whose TTL has elapsed) and is now held by <paramref name="owner"/>;
    /// <see langword="false"/> if a live lease on the key is held by another
    /// owner (the existing holder and TTL are left untouched).
    /// </returns>
    bool TryAcquire(string key, string owner, TimeSpan ttl);

    /// <summary>
    /// Attempts to extend the lease on <paramref name="key"/>.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> only if <paramref name="owner"/> still holds the
    /// live lease, in which case the TTL is extended to
    /// <paramref name="ttl"/> from now; <see langword="false"/> if the lease
    /// has expired or is held by another owner (state left untouched).
    /// </returns>
    bool Renew(string key, string owner, TimeSpan ttl);

    /// <summary>
    /// Attempts to release the lease on <paramref name="key"/>.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> only if <paramref name="owner"/> still holds the
    /// live lease, in which case the key becomes free; <see langword="false"/>
    /// if the lease has expired or is held by another owner — in particular,
    /// a previous owner whose lease expired and was re-acquired must NOT
    /// release the new holder's lease.
    /// </returns>
    bool Release(string key, string owner);
}
