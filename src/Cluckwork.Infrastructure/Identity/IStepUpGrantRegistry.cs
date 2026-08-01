namespace Cluckwork.Infrastructure.Identity;

// #308 — in-process bookkeeping for the two step-up-grant guarantees a bare
// stateless JWT cannot provide on its own: single-use (replay) and logout
// revocation. No new external infrastructure and no schema change — an
// in-memory, per-process store is consistent with the app's other in-memory
// security state (the per-IP rate limiter, #143). Bounded by the grant's own
// short lifetime via opportunistic pruning (InMemoryStepUpGrantRegistry). A
// process restart drops both tables, which only ever WIDENS what a
// not-yet-expired grant can do (fails OPEN across a restart, exactly like the
// rate limiter's buckets) — an accepted trade-off for a single-instance,
// single-farm-scoped deployment; a future multi-instance deployment would
// need to move this to a shared store.
//
// ATOMICITY (PR #336 review, 3rd round). The two guarantees are checked
// TOGETHER, in ONE call, deliberately. The interface used to expose the
// logout-epoch check and the jti consumption as two separate operations and
// StepUpGrantService called them back to back:
//
//     if (registry.IsRevokedByLogout(userId, issuedAt)) return denied;
//     if (!registry.TryConsume(jti, expiresAt, now))    return denied;
//
// Each operation was individually atomic (a ConcurrentDictionary apiece), but
// the PAIR was not, and the registry is a process-wide SINGLETON serving every
// concurrent request. A logout landing in the window between the two lines was
// therefore invisible to the validation already in flight:
//
//     T1 (validate): IsRevokedByLogout -> false   (no logout recorded yet)
//     T2 (logout):   RecordLogout(userId, now)    (completes here)
//     T1 (validate): TryConsume(jti, ...) -> true
//     T1 (validate): Success -> the privileged call proceeds
//
// The grant was minted BEFORE the logout, so the documented guarantee ("a
// grant captured before a legitimate logout cannot be used after it") says it
// must be refused — and the privileged operations behind it are precisely the
// ones that multiply durable account control (create another Owner, reset an
// Owner's password). The window is small but it is a real in-process race, not
// a theoretical one, and an attacker holding a stolen access token + grant can
// widen their odds by simply retrying.
//
// So the check-then-consume decision is a SINGLE registry operation
// (TryConsumeIfNotLoggedOut) whose implementation must take the same lock
// RecordLogout takes. Splitting it again — even into two individually
// thread-safe calls — reintroduces the race verbatim; that is what
// StepUpAuthTests' spy-registry test exists to catch.
public interface IStepUpGrantRegistry
{
    // The atomic step-up-grant admission decision: refuse the grant if userId
    // has logged out at or after issuedAt, otherwise mark jti used (expiring at
    // expiresAt) and admit it. Returns true ONLY when the grant was admitted.
    //
    // Ordering inside the operation is load-bearing: the logout-epoch check
    // runs FIRST and a logout-revoked grant leaves the jti UNCONSUMED. Consuming
    // it would be harmless today (it is refused either way) but it would burn a
    // replay slot on a token that never got to act, and it would make the two
    // failure modes distinguishable to anything that inspects the table.
    //
    // Returns a single bool, never a reason. That is the NON-ENUMERATING
    // contract from StepUpGrantService's threat model expressed in the type:
    // "revoked by logout" and "replayed" are indistinguishable to the caller,
    // so it cannot map them to different responses even by accident.
    //
    // issuedAt is compared against the recorded logout instant at FULL
    // precision, so the caller must pass a sub-second-accurate issuance time —
    // StepUpGrantService reads its own tick-precision claim for this, never the
    // grant's whole-second JWT nbf (see that class's "Revoked by logout" note).
    bool TryConsumeIfNotLoggedOut(
        Guid userId, Guid jti, DateTimeOffset issuedAt, DateTimeOffset expiresAt, DateTimeOffset now);

    // Records that userId logged out at `at`. Any grant issued AT OR BEFORE
    // this instant is subsequently refused by TryConsumeIfNotLoggedOut.
    // Implementations MUST serialise this against TryConsumeIfNotLoggedOut on
    // one synchronisation primitive — see the atomicity note above.
    void RecordLogout(Guid userId, DateTimeOffset at);

    // True when userId has a recorded logout at or after issuedAt. A read-only
    // observation of the logout epoch ALONE — it consumes nothing, so it is not
    // an admission decision and must never be used as one (that is the split
    // the atomicity note above forbids). It exists so callers that genuinely
    // only need to know "was this user's logout recorded?" — IdentityProvider's
    // logout-path regression tests — can ask without side effects.
    bool IsRevokedByLogout(Guid userId, DateTimeOffset issuedAt);
}
