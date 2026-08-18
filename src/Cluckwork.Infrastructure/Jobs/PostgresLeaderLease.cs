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
// Single-caller: TryAcquireAsync is only ever called from the one worker loop,
// sequentially, so no internal synchronisation is needed. DisposeAsync runs only
// after the hosted worker has stopped (hosted services stop before the DI container
// disposes its singletons).
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

    // Bounds the liveness probe so a half-open socket (server gone, no RST observed)
    // cannot stall the loop for Npgsql's default ~30s once per poll before the lease
    // gives up and opens a fresh connection.
    private const int LivenessProbeTimeoutSeconds = 5;

    private readonly string connectionString;
    private readonly ILogger<PostgresLeaderLease> logger;

    private NpgsqlConnection? connection;
    private bool held;

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

    public async Task<LeaseStatus> TryAcquireAsync(CancellationToken ct)
    {
        // Already leader: the lock is ours only for as long as our session is alive.
        // Prove the session is still up before re-affirming leadership — a dropped
        // connection means Postgres has already released the lock and another
        // instance may now hold it, so trusting `held` blindly is how a second
        // leader would appear. On a lost session, relinquish and fall through to a
        // fresh acquisition attempt.
        if (held)
        {
            if (await IsSessionAliveAsync(ct))
                return LeaseStatus.Leader;
            logger.LogWarning(
                "Leader-lease session was lost; relinquishing leadership and re-attempting acquisition.");
            await ResetAsync();
        }

        try
        {
            connection ??= await OpenConnectionAsync(ct);
            held = await TryLockAsync(connection, ct);
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

    private async Task<bool> IsSessionAliveAsync(CancellationToken ct)
    {
        try
        {
            if (connection is not { State: ConnectionState.Open })
                return false;
            await using var cmd = new NpgsqlCommand("SELECT 1", connection)
            {
                CommandTimeout = LivenessProbeTimeoutSeconds,
            };
            await cmd.ExecuteScalarAsync(ct);
            return true;
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

    private static async Task<bool> TryLockAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT pg_try_advisory_lock(@class, @obj)", connection);
        cmd.Parameters.AddWithValue("class", AdvisoryLockClassId);
        cmd.Parameters.AddWithValue("obj", AdvisoryLockObjectId);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
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
