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
public interface IStepUpGrantRegistry
{
    // Marks jti used, expiring at expiresAt. Returns false if it was already
    // used — a replay.
    bool TryConsume(Guid jti, DateTimeOffset expiresAt, DateTimeOffset now);

    // Records that userId logged out at `at`. Any grant issued AT OR BEFORE
    // this instant is subsequently treated as revoked by IsRevokedByLogout.
    void RecordLogout(Guid userId, DateTimeOffset at);

    // True when userId has a recorded logout at or after issuedAt.
    bool IsRevokedByLogout(Guid userId, DateTimeOffset issuedAt);
}
