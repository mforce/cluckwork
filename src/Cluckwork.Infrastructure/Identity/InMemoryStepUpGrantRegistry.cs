namespace Cluckwork.Infrastructure.Identity;

// #308 — see IStepUpGrantRegistry for the design rationale. Registered as a
// SINGLETON (one instance per process): replay tracking and logout epochs
// must be visible across every request/scope, unlike the request-scoped
// TenantContext/CurrentUserContext.
//
// SYNCHRONISATION (PR #336 review, 3rd round). ONE lock guards BOTH tables,
// and every public member takes it. The previous shape — a ConcurrentDictionary
// per table, no lock — made each individual operation atomic but left the
// admission decision (check the logout epoch, then consume the jti) a
// non-atomic PAIR that RecordLogout could interleave with; see the race trace
// in IStepUpGrantRegistry. Per-table atomicity is the wrong granularity: the
// invariant spans the two tables, so the critical section must too.
//
// A plain lock rather than finer-grained interlocking because the whole point
// is that the composite decision is indivisible, and because the contention
// profile does not justify anything cleverer: the critical sections are a
// dictionary probe and an insert, and the call rate is bounded by human
// logouts plus the two privileged admin operations a step-up grant unlocks —
// not by request volume. Prune walks the consumption table, which the same
// pruning keeps bounded by the grants outstanding within one 5-minute grant
// lifetime.
//
// Consequently the tables are plain Dictionary, not ConcurrentDictionary:
// under the lock the concurrent collections' own atomicity buys nothing and
// only obscures which primitive is actually establishing the guarantee.
public sealed class InMemoryStepUpGrantRegistry : IStepUpGrantRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, DateTimeOffset> _consumed = [];
    private readonly Dictionary<Guid, DateTimeOffset> _loggedOutAt = [];

    public bool TryConsumeIfNotLoggedOut(
        Guid userId, Guid jti, DateTimeOffset issuedAt, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        lock (_gate)
        {
            // Revocation first, and it returns WITHOUT consuming — a grant the
            // logout already killed must not burn its own replay slot. See the
            // interface note on why the ordering is part of the contract.
            if (RevokedByLogout(userId, issuedAt)) return false;

            Prune(now);
            return _consumed.TryAdd(jti, expiresAt);
        }
    }

    public void RecordLogout(Guid userId, DateTimeOffset at)
    {
        // The SAME lock the admission decision takes — that is the whole fix.
        // A RecordLogout that skipped it (or took a different one) would still
        // be internally safe and still lose the race it exists to win.
        lock (_gate)
        {
            _loggedOutAt[userId] =
                _loggedOutAt.TryGetValue(userId, out var existing) && existing > at ? existing : at;
        }
    }

    public bool IsRevokedByLogout(Guid userId, DateTimeOffset issuedAt)
    {
        lock (_gate) return RevokedByLogout(userId, issuedAt);
    }

    // At-or-before, deliberately: a grant minted in the same instant as the
    // logout is refused. Loosening this to `<` would accept a grant genuinely
    // minted earlier within the logout's own tick — see StepUpGrantService's
    // "Revoked by logout" note and the pair of boundary tests that pin BOTH
    // sides of it.
    private bool RevokedByLogout(Guid userId, DateTimeOffset issuedAt) =>
        _loggedOutAt.TryGetValue(userId, out var loggedOutAt) && issuedAt <= loggedOutAt;

    // Opportunistic: drop expired consumption records so long process uptime
    // doesn't grow this table with every step-up grant ever issued. Not
    // applied to _loggedOutAt — that table has at most one entry per user who
    // has ever logged out, bounded by the user count, not request volume.
    //
    // Collects first, removes after: mutating a Dictionary mid-enumeration is
    // only conditionally legal, and the caller already holds the lock, so
    // there is no benefit to being clever here. The list is allocated only
    // when something is actually expired — the overwhelmingly common case
    // allocates nothing.
    private void Prune(DateTimeOffset now)
    {
        List<Guid>? expired = null;
        foreach (var (jti, expiresAt) in _consumed)
            if (expiresAt < now)
                (expired ??= []).Add(jti);

        if (expired is null) return;
        foreach (var jti in expired) _consumed.Remove(jti);
    }
}
