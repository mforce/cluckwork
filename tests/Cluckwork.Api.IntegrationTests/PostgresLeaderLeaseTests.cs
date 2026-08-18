namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Infrastructure.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

// #271 — direct proof of the leader lease's contract: AT MOST ONE ACTIVE LEADER,
// with crash recovery and transaction-pooling backend-affinity. A dedicated Postgres
// container (advisory locks are lock-manager state, not tables — no schema is needed)
// keeps these tests isolated from the shared integration factory, whose own worker
// now holds the (271, 1) lock.
public sealed class PostgresLeaderLeaseTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(
        "postgres:18.4-trixie@sha256:3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    private PostgresLeaderLease NewLease() =>
        new(_postgres.GetConnectionString(), NullLogger<PostgresLeaderLease>.Instance);

    // Two leases contending for the same lock: exactly one becomes leader. This is
    // the load-bearing "at most one active leader" proof.
    [Fact]
    public async Task TwoLeases_ContendForOneLock_ExactlyOneAcquires()
    {
        await using var a = NewLease();
        await using var b = NewLease();

        var gotA = await a.TryAcquireAsync(CancellationToken.None);
        var gotB = await b.TryAcquireAsync(CancellationToken.None);

        Assert.Equal(LeaseStatus.Leader, gotA);
        Assert.Equal(LeaseStatus.Follower, gotB);
    }

    // Crash recovery: when the leader's session ends, its lock is released and a
    // survivor's next attempt wins.
    [Fact]
    public async Task Survivor_Reacquires_AfterLeaderReleases()
    {
        await using var survivor = NewLease();
        var leader = NewLease();

        Assert.Equal(LeaseStatus.Leader, await leader.TryAcquireAsync(CancellationToken.None));
        Assert.Equal(LeaseStatus.Follower, await survivor.TryAcquireAsync(CancellationToken.None));

        // The leader "crashes": disposing ends its session, releasing the lock.
        await leader.DisposeAsync();

        Assert.Equal(LeaseStatus.Leader, await AcquireUntilLeaderAsync(survivor));
    }

    // Loss mid-work: the leader's underlying session is killed out from under it. Its
    // next acquisition detects the lost session and relinquishes leadership, and the
    // freed lock is grabbable by another instance — the exact double-run guard.
    [Fact]
    public async Task LeaderLosesSession_RelinquishesLeadership_AndLockIsReacquirable()
    {
        await using var leader = NewLease();
        await using var competitor = NewLease();

        Assert.Equal(LeaseStatus.Leader, await leader.TryAcquireAsync(CancellationToken.None));
        var pid = leader.BackendProcessId;
        Assert.NotNull(pid);

        await TerminateBackendAsync(pid!.Value);

        // The freed lock proves the session really ended and released it. Backend
        // teardown after pg_terminate_backend is asynchronous, so poll rather than
        // demand it on the first try (avoids a CI flake).
        Assert.Equal(LeaseStatus.Leader, await AcquireUntilLeaderAsync(competitor));
        // The original leader detects it lost its session and no longer claims
        // leadership (the competitor now holds the lock).
        Assert.Equal(LeaseStatus.Follower, await leader.TryAcquireAsync(CancellationToken.None));
    }

    // Transaction-pooling safety (Codex P1): the connection stays alive but is now
    // served by a DIFFERENT backend than the one that took the advisory lock. The
    // held-path affinity check must detect the migration and relinquish, rather than
    // keep claiming leadership on the stale `held` flag. Observed via the connection
    // being torn down and reopened (a new backend PID). Removing the PID-equality
    // check makes this go red (no relinquish → same connection → same PID).
    [Fact]
    public async Task LeaderMigratedToAnotherBackend_RelinquishesTheStaleSession()
    {
        await using var leader = NewLease();

        Assert.Equal(LeaseStatus.Leader, await leader.TryAcquireAsync(CancellationToken.None));
        var backendBeforeMigration = leader.BackendProcessId;
        Assert.NotNull(backendBeforeMigration);

        // Simulate a transaction-pooling proxy: alive connection, different backend.
        leader.OverrideBackendPidProbe((_, _) => Task.FromResult(-1));

        // Held-path re-affirm sees the mismatch, relinquishes (disposes the session),
        // and reacquires on a fresh connection — a different backend PID.
        await leader.TryAcquireAsync(CancellationToken.None);
        var backendAfterMigration = leader.BackendProcessId;

        Assert.NotEqual(backendBeforeMigration, backendAfterMigration);
    }

    // Polls acquisition until this lease wins the lock or a short deadline passes —
    // absorbs the asynchronous lock-release after a session ends.
    private static async Task<LeaseStatus> AcquireUntilLeaderAsync(PostgresLeaderLease lease)
    {
        LeaseStatus status = LeaseStatus.Follower;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            status = await lease.TryAcquireAsync(CancellationToken.None);
            if (status == LeaseStatus.Leader)
                return status;
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }
        return status;
    }

    private async Task TerminateBackendAsync(int pid)
    {
        await using var admin = new NpgsqlConnection(_postgres.GetConnectionString());
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT pg_terminate_backend(@pid)", admin);
        cmd.Parameters.AddWithValue("pid", pid);
        await cmd.ExecuteScalarAsync();
    }
}
