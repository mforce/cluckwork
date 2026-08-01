namespace Cluckwork.Api.IntegrationTests;

using System.Data.Common;
using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

// #269 review round 2 (codex 3696740950, P1) — "reacquire the bootstrap lock
// after read retries".
//
// FirstRunAdminService guards "exactly one first-run Owner" with a
// SESSION-scoped pg_advisory_lock. Round 1 stopped the CREATE from being
// replayed (SingleAttemptExecution inside AmbientTransaction), but the two
// READS that decide whether to create at all — GetUsersInRoleAsync and the
// conflicting-email lookup — were still ordinary EF units of work, and so were
// still retried by NpgsqlRetryingExecutionStrategy. A retry reconnects; the
// session-scoped lock lives on the PHYSICAL connection, so reconnecting drops
// it. The method then walked on into the create holding nothing, and two
// concurrent `bootstrap-admin` invocations with different emails could each
// observe "no Owner" and each mint one.
//
// Unlike round 1 — which conceded it had no deterministic test for this — these
// tests do drop a real connection: BootstrapLockContinuityInterceptor reads the
// pinned session's backend pid off the executing NpgsqlConnection and
// pg_terminate_backend()s it from an INDEPENDENT connection, which is exactly
// what a managed-Postgres failover does and genuinely releases the advisory
// lock. Lock ownership is then asserted directly against pg_locks rather than
// inferred. What each test does and does not prove is stated on the test.
public sealed class BootstrapLockContinuityFactory : CluckworkWebApplicationFactory
{
    public BootstrapLockContinuityInterceptor Interceptor { get; }

    public BootstrapLockContinuityFactory() => Interceptor = new(() => ConnectionString);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        // Keep the (deliberately unwanted) retry backoff short, so a test that
        // regresses into retrying still finishes in seconds rather than
        // stalling the suite on the default exponential delay.
        builder.UseSetting("Database:Resilience:MaxRetryDelaySeconds", "1");
        builder.ConfigureTestServices(services =>
            services.AddDbContext<AppDbContext>((_, options) => options.AddInterceptors(Interceptor)));
    }
}

// Fault injection for the bootstrap critical section. Everything it does is
// keyed off command text, so it is inert for every other test in the suite.
public sealed class BootstrapLockContinuityInterceptor(Func<string> connectionString) : DbCommandInterceptor
{
    // "pg_advisory_unlock" does NOT contain "pg_advisory_lock", so this matches
    // the acquire and never the release.
    private const string AcquireLockSql = "pg_advisory_lock";
    private const string UsersTable = "\"AspNetUsers\"";
    private const string CreateUserSql = "INSERT INTO \"AspNetUsers\"";

    // Terminate the pinned backend from a second connection the moment the
    // advisory lock has been acquired — a REAL connection loss, which really
    // does release the lock.
    public volatile bool KillBackendAfterAcquiringTheLock;

    // Release the lock on the session that holds it, without dropping the
    // connection. A stand-in for "the lock is gone but the session is not",
    // used to exercise the ownership assertion on its own.
    public volatile bool ReleaseLockAfterAcquiringIt;

    // Release the lock AND immediately take it on an independent, long-lived
    // connection: the shape a real concurrent `bootstrap-admin` produces once
    // this invocation loses its connection — the lock IS held, just not by us.
    public volatile bool HandLockToAnotherSessionAfterAcquiringIt;

    private NpgsqlConnection? _handOff;

    private int _faultInjected;
    private int _killedPid;
    private int _userTableCommandsAfterFault;
    private int _createAttempts;
    private int _lockHeldAtCreate = -1;

    // Backend pid actually terminated (0 = the kill never happened).
    public int KilledPid => Volatile.Read(ref _killedPid);

    // Commands touching AspNetUsers that EF issued AFTER the fault. Non-zero
    // means the region carried on past the connection loss — i.e. onto a
    // reconnected session that no longer holds the lock.
    public int UserTableCommandsAfterFault => Volatile.Read(ref _userTableCommandsAfterFault);

    public int CreateAttempts => Volatile.Read(ref _createAttempts);

    // Whether the advisory lock was held BY THE BACKEND RUNNING THE CREATE at
    // the instant the create was issued, observed from an independent
    // connection. null = no create was ever attempted.
    public bool? LockHeldByCreateBackend =>
        Volatile.Read(ref _lockHeldAtCreate) is var v && v < 0 ? null : v == 1;

    private static int BackendPid(DbCommand command) =>
        ((NpgsqlConnection)command.Connection!).ProcessID;

    private async Task OnExecutingAsync(DbCommand command)
    {
        var text = command.CommandText;

        if (Volatile.Read(ref _faultInjected) == 1 &&
            text.Contains(UsersTable, StringComparison.Ordinal))
            Interlocked.Increment(ref _userTableCommandsAfterFault);

        if (!text.Contains(CreateUserSql, StringComparison.Ordinal)) return;

        Interlocked.Increment(ref _createAttempts);
        Volatile.Write(ref _lockHeldAtCreate, await LockHeldByAsync(BackendPid(command)) ? 1 : 0);
    }

    private async Task OnExecutedAsync(DbCommand command)
    {
        if (!command.CommandText.Contains(AcquireLockSql, StringComparison.Ordinal)) return;
        if (Interlocked.CompareExchange(ref _faultInjected, 1, 0) != 0) return;

        if (KillBackendAfterAcquiringTheLock)
        {
            var pid = BackendPid(command);
            await TerminateBackendAsync(pid);
            Volatile.Write(ref _killedPid, pid);
            return;
        }

        if (ReleaseLockAfterAcquiringIt || HandLockToAnotherSessionAfterAcquiringIt)
        {
            await using (var release = new NpgsqlCommand(
                "SELECT pg_advisory_unlock_all()", (NpgsqlConnection)command.Connection!))
                await release.ExecuteNonQueryAsync();

            if (!HandLockToAnotherSessionAfterAcquiringIt) return;

            // Ours is released first, so this cannot block. Held for the rest
            // of the test by keeping the connection open.
            _handOff = new NpgsqlConnection(connectionString());
            await _handOff.OpenAsync();
            await using var take = new NpgsqlCommand("SELECT pg_advisory_lock(283, 1)", _handOff);
            await take.ExecuteNonQueryAsync();
            return;
        }

        // Nothing armed — don't consume the one-shot latch.
        Interlocked.Exchange(ref _faultInjected, 0);
    }

    public async Task ReleaseHandOffAsync()
    {
        if (_handOff is null) return;
        await _handOff.DisposeAsync();
        _handOff = null;
    }

    private async Task TerminateBackendAsync(int pid)
    {
        await using var probe = new NpgsqlConnection(connectionString());
        await probe.OpenAsync();

        await using (var kill = new NpgsqlCommand("SELECT pg_terminate_backend($1)", probe))
        {
            kill.Parameters.AddWithValue(pid);
            await kill.ExecuteScalarAsync();
        }

        // pg_terminate_backend returns as soon as the signal is sent; wait for
        // the backend to actually be gone so the lock is provably released
        // before the next command goes out.
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var gone = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE pid = $1", probe);
            gone.Parameters.AddWithValue(pid);
            if ((long)(await gone.ExecuteScalarAsync())! == 0) return;
            await Task.Delay(20);
        }

        throw new InvalidOperationException($"backend {pid} did not terminate within the test's budget");
    }

    private async Task<bool> LockHeldByAsync(int pid)
    {
        await using var probe = new NpgsqlConnection(connectionString());
        await probe.OpenAsync();
        await using var held = new NpgsqlCommand(
            "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' " +
            "AND classid = 283 AND objid = 1 AND pid = $1 AND granted", probe);
        held.Parameters.AddWithValue(pid);
        return (long)(await held.ExecuteScalarAsync())! > 0;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await OnExecutingAsync(command);
        return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override async ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        await OnExecutedAsync(command);
        return await base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        await OnExecutingAsync(command);
        return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override async ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        await OnExecutedAsync(command);
        return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }
}

// FirstRunAdminService provisions the single fixed SeedDefaults.AccountId, so
// every class below needs its OWN throwaway database — two tests sharing one
// could never both observe "no Owner yet". xUnit builds a separate
// IClassFixture instance (and therefore a separate container) per test class.
public sealed class BootstrapBackendLossFailsClosedTests
    : IClassFixture<BootstrapLockContinuityFactory>
{
    private readonly BootstrapLockContinuityFactory _factory;

    public BootstrapBackendLossFailsClosedTests(BootstrapLockContinuityFactory factory)
    {
        _factory = factory;
        _ = _factory.Services; // force host + migration before the fault is armed
    }

    // PROVES: with the pinned backend genuinely terminated right after the
    // advisory lock was taken (so the lock is really gone), provisioning stops
    // there. It does not reconnect and carry on: no AspNetUsers command is
    // issued after the loss, no create is attempted, no Owner exists, and the
    // failure propagates to the CLI (exit 1).
    //
    // The transience assertion is load-bearing: if Npgsql did not classify the
    // failure transient, nothing would have been retried even BEFORE the fix
    // and the test would pass for the wrong reason.
    //
    // DOES NOT PROVE: that two concurrent invocations cannot both create an
    // Owner. That is a genuine race, not schedulable from a test. What this
    // pins is the enabling condition the finding turns on — continuing past a
    // connection loss onto a session that no longer holds the lock.
    [Fact]
    public async Task LosingTheBackendUnderTheLock_StopsThere_InsteadOfContinuingOnAReconnectedSession()
    {
        _factory.Interceptor.KillBackendAfterAcquiringTheLock = true;
        var email = $"lock-continuity-{Guid.NewGuid():N}@test.local";

        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<FirstRunAdminService>();

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => service.ProvisionAsync(email));

        // Snapshot before any verification query of our own reaches AspNetUsers.
        var userCommandsAfterFault = _factory.Interceptor.UserTableCommandsAfterFault;
        var createAttempts = _factory.Interceptor.CreateAttempts;

        Assert.NotEqual(0, _factory.Interceptor.KilledPid);
        Assert.True(
            IsTransient(thrown),
            $"the injected connection loss must be one Npgsql classifies TRANSIENT, or the retry " +
            $"this test is about would never have happened; got: {thrown}");

        Assert.Equal(0, userCommandsAfterFault);
        Assert.Equal(0, createAttempts);

        var usersInDefaultAccount = await _factory.WithTenantScopeAsync(SeedDefaults.AccountId,
            db => db.Users.IgnoreQueryFilters().CountAsync(u => u.AccountId == SeedDefaults.AccountId));
        Assert.Equal(0, usersInDefaultAccount);
    }

    private static bool IsTransient(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is NpgsqlException { IsTransient: true }) return true;
        return false;
    }
}

public sealed class BootstrapLockLostBeforeCreateTests
    : IClassFixture<BootstrapLockContinuityFactory>
{
    private readonly BootstrapLockContinuityFactory _factory;

    public BootstrapLockLostBeforeCreateTests(BootstrapLockContinuityFactory factory)
    {
        _factory = factory;
        _ = _factory.Services;
    }

    // PROVES: the create is gated on the lock being observably held. The lock
    // is released out from under the critical section (pg_advisory_unlock_all
    // on the service's own session) after it was acquired; the checks then run
    // unguarded, and the create must be refused with a clear failure rather
    // than proceeding — Result.Failure, no Owner row, no create attempted.
    //
    // DOES NOT PROVE: how the lock came to be lost. Releasing it on the same
    // session is a stand-in for "the connection was replaced", chosen because
    // it isolates the ownership assertion from the single-attempt boundary —
    // the real connection loss is covered by the test above. It is the same
    // observable state either way: the session about to run the create does
    // not hold the lock.
    [Fact]
    public async Task LockNotHeldWhenTheCreateIsReached_FailsClosed_AndCreatesNoOwner()
    {
        _factory.Interceptor.ReleaseLockAfterAcquiringIt = true;
        var email = $"lock-lost-{Guid.NewGuid():N}@test.local";

        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<FirstRunAdminService>();

        var result = await service.ProvisionAsync(email);

        var createAttempts = _factory.Interceptor.CreateAttempts;

        Assert.True(result.IsFailure, "provisioning must refuse to create an Owner without the lock");
        Assert.Equal("Bootstrap.LockLost", result.Error.Code);
        Assert.Equal(0, createAttempts);

        var usersInDefaultAccount = await _factory.WithTenantScopeAsync(SeedDefaults.AccountId,
            db => db.Users.IgnoreQueryFilters().CountAsync(u => u.AccountId == SeedDefaults.AccountId));
        Assert.Equal(0, usersInDefaultAccount);
    }
}

public sealed class BootstrapLockHeldByAnotherSessionTests
    : IClassFixture<BootstrapLockContinuityFactory>
{
    private readonly BootstrapLockContinuityFactory _factory;

    public BootstrapLockHeldByAnotherSessionTests(BootstrapLockContinuityFactory factory)
    {
        _factory = factory;
        _ = _factory.Services;
    }

    // PROVES: the guard asks whether WE hold the lock, not merely whether the
    // lock is held. This is the state a real concurrent `bootstrap-admin`
    // leaves behind the instant this invocation loses its connection — the
    // lock is held, by the OTHER invocation — and it is precisely the state in
    // which creating an Owner produces the second farm Owner the finding is
    // about. It must be refused.
    //
    // DOES NOT PROVE: that the two invocations are genuinely concurrent. The
    // competing holder here is a plain second connection taking the same lock,
    // not a second FirstRunAdminService; what is reproduced is the lock state,
    // which is the only thing the guard can observe anyway.
    [Fact]
    public async Task LockHeldByADifferentSession_IsNotMistakenForOurOwn_AndTheCreateIsRefused()
    {
        _factory.Interceptor.HandLockToAnotherSessionAfterAcquiringIt = true;
        var email = $"lock-stolen-{Guid.NewGuid():N}@test.local";

        try
        {
            using var scope = _factory.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<FirstRunAdminService>();

            var result = await service.ProvisionAsync(email);

            var createAttempts = _factory.Interceptor.CreateAttempts;

            Assert.True(result.IsFailure,
                "another session holding the lock is not this session holding it");
            Assert.Equal("Bootstrap.LockLost", result.Error.Code);
            Assert.Equal(0, createAttempts);

            var usersInDefaultAccount = await _factory.WithTenantScopeAsync(SeedDefaults.AccountId,
                db => db.Users.IgnoreQueryFilters().CountAsync(u => u.AccountId == SeedDefaults.AccountId));
            Assert.Equal(0, usersInDefaultAccount);
        }
        finally
        {
            await _factory.Interceptor.ReleaseHandOffAsync();
        }
    }
}

public sealed class BootstrapLockHeldAtCreateTests
    : IClassFixture<BootstrapLockContinuityFactory>
{
    private readonly BootstrapLockContinuityFactory _factory;

    public BootstrapLockHeldAtCreateTests(BootstrapLockContinuityFactory factory)
    {
        _factory = factory;
        _ = _factory.Services;
    }

    // The positive side, and the reason the two tests above cannot pass
    // vacuously: on an undisturbed run the advisory lock IS observably held —
    // by the very backend that issues the create, read straight out of
    // pg_locks from an independent connection. A guard whose predicate never
    // matched anything would fail this one.
    [Fact]
    public async Task HappyPath_TheAdvisoryLockIsHeldByTheBackendThatRunsTheCreate()
    {
        var email = $"lock-held-{Guid.NewGuid():N}@test.local";

        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<FirstRunAdminService>();

        var result = await service.ProvisionAsync(email);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Description : null);
        Assert.False(result.Value.WasAlreadyProvisioned);
        Assert.Equal(1, _factory.Interceptor.CreateAttempts);
        Assert.True(_factory.Interceptor.LockHeldByCreateBackend);
    }
}
