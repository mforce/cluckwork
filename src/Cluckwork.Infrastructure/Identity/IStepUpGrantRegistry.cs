namespace Cluckwork.Infrastructure.Identity;

// #308/#338 — bookkeeping for the two step-up-grant guarantees a bare stateless
// JWT cannot provide: single-use (replay) and logout revocation. #338 moved both
// out of the process into shared storage of OPPOSITE shapes:
//   - single-use  -> IClaimOnceStore (#543): a cache-shaped, self-expiring claim,
//     Redis-backed across replicas or in-process on a single instance.
//   - logout epoch -> ApplicationUser.StepUpLogoutEpoch: a durable per-user
//     integer, read fresh per validation like CredentialEpoch (#364). The grant
//     carries the epoch it was issued under; logout increments the epoch; a grant
//     is admitted only while its epoch still equals the user's current one. An
//     INTEGER compared for equality, never a timestamp — so logout revocation is
//     immune to wall-clock skew between the issuing replica and the logging-out
//     replica (the #338 review defect that a timestamp comparison shipped).
//
// ATOMICITY (originally PR #336; re-established for the two-store split in #338).
// The two guarantees are decided TOGETHER, in ONE call, deliberately. The
// in-memory registry closed the check-then-consume race with a single lock over
// both tables. No lock can span Redis and Postgres, so the guarantee is now
// re-established by ORDERING inside TryConsumeIfNotLoggedOutAsync: consume the
// claim FIRST, then read the epoch. See PersistentStepUpGrantRegistry.
//
//     T1 (validate): epoch read -> N (grant's epoch, not revoked yet)
//     T2 (logout):   RecordLogout(userId)  -> epoch becomes N+1
//     T1 (validate): consume(jti) -> true
//     T1 (validate): Success -> the privileged call proceeds   // the bug, if read-first
//
// Splitting the decision back into two calls, or reading the epoch before
// consuming, reintroduces the race; a deterministic test in
// StepUpGrantRegistrySharedStoreTests pins the order.
public interface IStepUpGrantRegistry
{
    // The atomic step-up-grant admission decision: mark jti used (expiring at
    // expiresAt) and admit the grant UNLESS the user's step-up logout epoch has
    // advanced past the one the grant was issued under. Returns true ONLY when
    // the grant was admitted.
    //
    // Ordering is load-bearing (#338): the claim is consumed FIRST, then the
    // epoch is read. A logout incrementing the epoch between the two is caught by
    // the read; one after is genuinely after admission. A revoked grant still
    // burns its one-time claim slot — harmless (refused either way) and it MUST
    // stay indistinguishable to the caller.
    //
    // Returns a single bool, never a reason: the NON-ENUMERATING contract —
    // "revoked by logout" and "replayed" are indistinguishable to the caller.
    Task<bool> TryConsumeIfNotLoggedOutAsync(
        Guid userId, Guid jti, int grantEpoch, DateTimeOffset expiresAt,
        DateTimeOffset now, CancellationToken ct = default);

    // Records that userId logged out by advancing the user's step-up logout epoch
    // by one. Monotonic by construction. Idempotency note: a single logout can
    // reach this twice (the cookie owner and the authenticated bearer are recorded
    // independently — IdentityProvider), incrementing the epoch more than once for
    // one logout; that is harmless because only equality with the grant's embedded
    // epoch matters, never the absolute value.
    Task RecordLogoutAsync(Guid userId, CancellationToken ct = default);

    // True when the user's current step-up logout epoch has advanced past
    // grantEpoch (i.e. a logout was recorded since the grant was issued). A
    // read-only observation of the epoch ALONE — it consumes nothing, so it is
    // not an admission decision and must never be used as one. It exists so
    // IdentityProvider's logout-path regression tests can ask without side effects.
    Task<bool> IsRevokedByLogoutAsync(
        Guid userId, int grantEpoch, CancellationToken ct = default);
}
