namespace Cluckwork.Infrastructure.Jobs;

// #271 — the outcome of one leadership-acquisition attempt. A first-class result
// (not a bool) so the worker can tell "another instance holds the lock" (Follower,
// a healthy steady state) apart from "I could not even ask" (Faulted, a fault that
// must degrade /health). Conflating the two let a DB outage read as a healthy
// follower and silently stop all background work behind a green health check — the
// exact #69 stall-detection guarantee this must not break.
public enum LeaseStatus
{
    // This instance holds the lease: run the poll and the sweeps.
    Leader,
    // Another instance holds the lease: stand down. The loop is alive and healthy —
    // a follower does no work by design.
    Follower,
    // The acquisition attempt itself faulted (e.g. the DB is unreachable): back off
    // and do NOT stamp the heartbeat, so a sustained fault degrades /health.
    Faulted,
}

// #271 — the background worker's single-runner gate: at most one API instance (the
// "leader") runs the durable-job poll and the recurring sweeps. See
// PostgresLeaderLease for the mechanism and the exact contract (AT MOST ONE ACTIVE
// LEADER, never "exactly once").
public interface ILeaderLease
{
    // The leadership status of THIS instance after the call. Safe to call every
    // poll: a current leader re-affirms (re-verifying its session) without stacking
    // the lock; a follower retries acquisition; a fault is reported as Faulted, not
    // thrown, so the worker never lets it reach BackgroundServiceExceptionBehavior.
    Task<LeaseStatus> TryAcquireAsync(CancellationToken ct);
}

// A lease that is always the leader — for a deploy that is single-instance by
// construction, and for the worker's resilience unit tests. Deliberately NOT a
// constructor default: the worker REQUIRES an explicit ILeaderLease, so a host that
// forgets to register one fails at startup (fail-closed) rather than silently
// running every replica as leader and re-introducing the #271 double-run.
public sealed class AlwaysLeaderLease : ILeaderLease
{
    public Task<LeaseStatus> TryAcquireAsync(CancellationToken ct) =>
        Task.FromResult(LeaseStatus.Leader);
}

// Hands the already-normalised, TLS-floor-validated connection string (registered
// by AddCluckworkPersistence) to PostgresLeaderLease without a second configuration
// lookup — the lease opens its own dedicated, non-pooled connection from this exact
// string. A tiny typed wrapper so DI injects the right string rather than an ambient
// one.
public sealed record LeaderLeaseConnectionString(string Value);
