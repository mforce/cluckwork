namespace Cluckwork.Infrastructure.Jobs;

using System.Data;
using Microsoft.Extensions.Logging;
using Npgsql;

// #271 — the background worker's single-runner gate. Exactly one API instance may
// run the durable-job poll and the three recurring sweeps; a session-scoped
// Postgres advisory lock provides that mutual exclusion across replicas without a
// shared clock.
//
// Deliberately a Postgres advisory lock, NOT one of #543's Redis ports (epic #530
// decision 13): a Redis lock's failure mode is that the resilient decorator falls
// back per-replica on a Redis outage — precisely the double-execution this lease
// exists to prevent. A Postgres session lock has the opposite, safe failure mode:
// if the holding connection dies the lock is released and a survivor takes over,
// and it is never held by two sessions at once.
//
// The contract is AT MOST ONE ACTIVE LEADER, never "exactly once". A session lock
// lives on one physical connection: if that connection drops, Postgres ends the
// session and releases the lock, so a leader can lose leadership while its work is
// still in flight. Durable jobs are therefore at-least-once and their handlers must
// be idempotent; the three current sweeps are idempotent deletes / an idempotent
// Submitted->Locked transition. Do NOT write "exactly once" into this class, its
// tests, or the finish line.
//
// The lock is held on a DEDICATED, long-lived connection — never an EF pooled
// connection, which opens and closes per operation and would release a session lock
// the instant it returned to the pool (the same reasoning FirstRunAdminService pins
// its connection open for the #283 bootstrap lock). The connection is also
// NON-POOLED: it is held for the whole process lifetime, so pooling buys nothing,
// and sharing the app's pool (Npgsql keys pools by connection-string text) would
// let this permanently-held connection consume a pool slot and starve request
// traffic — fatal under a deliberately shrunk pool.
//
// TRANSACTION-POOLING SAFETY (Codex P1). This lease requires a SESSION-pinned
// Postgres endpoint — a direct connection or a session-pooled proxy. Under a
// TRANSACTION-pooling proxy (e.g. PgBouncer in transaction mode) Pooling=false only
// disables Npgsql's client pool; the proxy still multiplexes this one client
// connection across different server backends between statements. The advisory lock
// is cluster-global but bound to the backend session that took it, so a later poll
// served by a different backend no longer holds it — and the proxy could even hand
// the lock-holding backend to another replica, whose re-entrant pg_try_advisory_lock
// would then also succeed. To stay fail-safe we capture the backend PID that took
// the lock (in the SAME statement as the acquire, so it is truly that backend) and
// re-verify it on every poll; a mismatch means our commands have migrated off the
// lock's session, so we relinquish leadership rather than run as a second leader.
// The net effect under transaction pooling is that no instance holds leadership
// stably and /health degrades (visible), never a silent double-run. Making the
// lease WORK under transaction pooling — a dedicated session-pinned lease endpoint —
// is deliberately a follow-up, not this slice.
//
// Single-caller: TryAcquireAsync is only ever called from the one worker loop,
// sequentially, so no internal synchronisation is needed. DisposeAsync runs only
// after the hosted worker has stopped (barring a shutdown-timeout, in which case an
// in-flight call fails into the caught path below — never a crash or double-lead).
public sealed class PostgresLeaderLease : ILeaderLease, IAsyncDisposable
{
    // Two-int pg_try_advisory_lock(int, int) form. Postgres documents the two-key
    // and single-bigint advisory-lock forms as DISTINCT key spaces — a two-key lock
    // never conflicts with a one-key lock even when the bits coincide — so this can
    // never collide with a future single-bigint advisory lock. classId is the issue
    // number for traceability; objId leaves room for more locks under the same class
    // later. Mirrors the #283 bootstrap lock's key convention (class 283).
    private const int AdvisoryLockClassId = 271;
    private const int AdvisoryLockObjectId = 1;

    // Bounds the liveness/affinity probe so a half-open socket (server gone, no RST
    // observed) cannot stall the loop for Npgsql's default ~30s once per poll before
    // the lease gives up and opens a fresh connection.
    private const int LivenessProbeTimeoutSeconds = 5;

    private readonly string connectionString;
    private readonly ILogger<PostgresLeaderLease> logger;

    private NpgsqlConnection? connection;
    private bool held;
    // The server backend PID that actually took the advisory lock (captured in the
    // same statement as the acquire). Leadership is only re-affirmed while our
    // connection is still served by this exact backend.
    private int lockBackendPid;

    // Test seam: how the lease reads the backend PID currently serving its
    // connection. Defaults to a real round trip; a test overrides it to simulate a
    // transaction-pooling proxy migrating the connection to a different backend.
    private Func<NpgsqlConnection, CancellationToken, Task<int>>? backendPidProbeOverride;

    public PostgresLeaderLease(string connectionString, ILogger<PostgresLeaderLease> logger)
    {
        // A NON-POOLED connection outside the app pool (see the class comment). The
        // builder round-trip is safe here — unlike the #332 case, which guards the
        // OPERATOR'S RAW string, this string is the ALREADY-normalised, Npgsql
        // key-value form the app itself opens with (Npgsql.Open re-parses it through
        // the same builder on every connect), so if it were not builder-parseable
        // the app could not connect at all.
        this.connectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            ApplicationName = "cluckwork-leader-lease",
        }.ConnectionString;
        this.logger = logger;
    }

    // Test-only: the backend PID of the connection currently holding the lease, so a
    // test can terminate exactly this session to simulate a leader crash.
    internal int? BackendProcessId =>
        connection is { State: ConnectionState.Open } c ? c.ProcessID : null;

    // Test-only: override how the current backend PID is read, to simulate a
    // transaction-pooling proxy serving this connection from a different backend.
    internal void OverrideBackendPidProbe(Func<NpgsqlConnection, CancellationToken, Task<int>> probe) =>
        backendPidProbeOverride = probe;

    public async Task<LeaseStatus> TryAcquireAsync(CancellationToken ct)
    {
        // Already leader: the lock is ours only while our session is alive AND our
        // connection is still served by the backend that took the lock. Prove both
        // before re-affirming — a dropped connection means Postgres released the
        // lock, and a migrated backend (transaction pooling) means we are no longer
        // on the lock's session; either way trusting `held` blindly is how a second
        // leader appears. On loss, relinquish and fall through to a fresh attempt.
        if (held)
        {
            if (await HoldsLockBackendAsync(ct))
                return LeaseStatus.Leader;
            logger.LogWarning(
                "Leader-lease session/backend was lost; relinquishing leadership and re-attempting acquisition.");
            await ResetAsync();
        }

        try
        {
            connection ??= await OpenConnectionAsync(ct);
            var (acquired, backendPid) = await TryLockAsync(connection, ct);
            held = acquired;
            if (acquired)
                lockBackendPid = backendPid;
            return held ? LeaseStatus.Leader : LeaseStatus.Follower;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A fault acquiring the lock must not crash the host, and must NOT be
            // mistaken for a healthy follower: report Faulted so the worker backs off
            // and lets /health degrade (a `Follower` would stamp a healthy heartbeat
            // and hide a full DB outage). Drop the possibly-broken connection so the
            // next attempt opens a clean one.
            logger.LogWarning(ex, "Leader-lease acquisition attempt failed; will retry next cycle.");
            await ResetAsync();
            return LeaseStatus.Faulted;
        }
    }

    // Alive AND still on the lock's backend. False (relinquish) if the connection is
    // down, the probe throws, or the serving backend PID no longer matches the one
    // that took the lock (transaction-pooling migration).
    private async Task<bool> HoldsLockBackendAsync(CancellationToken ct)
    {
        try
        {
            if (connection is not { State: ConnectionState.Open })
                return false;
            var currentPid = await ReadCurrentBackendPidAsync(connection, ct);
            return currentPid == lockBackendPid;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task<int> ReadCurrentBackendPidAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        if (backendPidProbeOverride is not null)
            return await backendPidProbeOverride(connection, ct);

        await using var cmd = new NpgsqlCommand("SELECT pg_backend_pid()", connection)
        {
            CommandTimeout = LivenessProbeTimeoutSeconds,
        };
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    // Acquires the lock and reads the holding backend's PID in ONE statement, so the
    // captured PID is guaranteed to be the backend the lock lives on (not a later,
    // possibly-migrated one).
    private static async Task<(bool Acquired, int BackendPid)> TryLockAsync(
        NpgsqlConnection connection, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT pg_try_advisory_lock(@class, @obj), pg_backend_pid()", connection);
        cmd.Parameters.AddWithValue("class", AdvisoryLockClassId);
        cmd.Parameters.AddWithValue("obj", AdvisoryLockObjectId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetBoolean(0), reader.GetInt32(1));
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(connectionString);
        try
        {
            await conn.OpenAsync(ct);
            return conn;
        }
        catch
        {
            // A failed OpenAsync must not abandon the connection object — dispose it
            // deterministically rather than leaving it to GC.
            await conn.DisposeAsync();
            throw;
        }
    }

    private async Task ResetAsync()
    {
        held = false;
        if (connection is null)
            return;
        var conn = connection;
        connection = null;
        // Disposing the connection ends the session, which releases any advisory lock
        // it held — an explicit pg_advisory_unlock is therefore unnecessary.
        try
        {
            await conn.DisposeAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Leader-lease connection dispose failed; ignoring.");
        }
    }

    public async ValueTask DisposeAsync() => await ResetAsync();
}
