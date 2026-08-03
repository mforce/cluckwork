namespace Cluckwork.Api.IntegrationTests.Infrastructure;

using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

// #269 review — deterministic stand-ins for the two failure shapes the retry
// boundary has to be reasoned about, neither of which can be produced reliably
// by actually killing a Postgres container mid-request:
//
//   * FAIL-BEFORE — the command never reached the server. Nothing committed;
//     a retry is safe as far as the DATABASE is concerned.
//   * FAIL-AFTER — the command ran and committed, and it is the ACKNOWLEDGMENT
//     that was lost (the "ambiguous commit"). A retry re-runs work that is
//     already durable. This is the shape every one of the four findings turns
//     on, and the only honest way to test them.
//
// A PostgresException is constructed directly (public 4-arg ctor:
// messageText/severity/invariantSeverity/sqlState) rather than produced by a
// real dropped connection: what NpgsqlRetryingExecutionStrategy.ShouldRetryOn
// checks is PostgresException.IsTransient, computed purely from SqlState.
// CannotConnectNow ("57P03") is transient — a generic exception would not be
// retried at all and would give a test that passes for the wrong reason.
public static class TransientFault
{
    public const string SqlState = PostgresErrorCodes.CannotConnectNow;

    public static PostgresException Create() => new(
        "simulated transient connection loss (test fault injection)", "FATAL", "FATAL", SqlState);

    // EF wraps a store failure in DbUpdateException, which the execution
    // strategy unwraps to classify; assertions need the same unwrapping.
    public static PostgresException? InnermostPostgres(Exception? exception)
    {
        PostgresException? found = null;
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is PostgresException postgres) found = postgres;
        return found;
    }
}

// Fires on the first command whose text contains an armed fragment, either
// before or after the command actually executes, then disarms itself. Counts
// every matching command that reached the interceptor, so a test can assert
// how many times a region was executed — that count IS the retry boundary.
public sealed class TransientCommandFaultInterceptor : DbCommandInterceptor
{
    private volatile string? _fragment;
    private volatile bool _failAfterExecution;
    private volatile bool _repeat;
    private int _armed;
    private int _matches;

    // Commands matching the armed fragment that reached this interceptor.
    public int Matches => Volatile.Read(ref _matches);

    public void Arm(string commandTextFragment, bool afterExecution)
    {
        Interlocked.Exchange(ref _matches, 0);
        _failAfterExecution = afterExecution;
        _repeat = false;
        _fragment = commandTextFragment;
        Interlocked.Exchange(ref _armed, 1);
    }

    // Never lets a matching command through — for asserting that retries are
    // BOUNDED rather than that one of them eventually succeeds.
    public void ArmAlways(string commandTextFragment)
    {
        Interlocked.Exchange(ref _matches, 0);
        _failAfterExecution = false;
        _repeat = true;
        _fragment = commandTextFragment;
        Interlocked.Exchange(ref _armed, 1);
    }

    public void Disarm()
    {
        Interlocked.Exchange(ref _armed, 0);
        _repeat = false;
        _fragment = null;
    }

    private void MaybeFail(DbCommand command, bool afterExecution)
    {
        var fragment = _fragment;
        if (fragment is null) return;
        if (!command.CommandText.Contains(fragment, StringComparison.OrdinalIgnoreCase)) return;

        // Count on the way IN, exactly once per command, whichever hook fires.
        if (!afterExecution) Interlocked.Increment(ref _matches);
        if (afterExecution != _failAfterExecution) return;
        if (_repeat)
        {
            if (Volatile.Read(ref _armed) != 1) return;
        }
        else if (Interlocked.CompareExchange(ref _armed, 0, 1) != 1)
        {
            return;
        }

        throw TransientFault.Create();
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        MaybeFail(command, afterExecution: false);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        MaybeFail(command, afterExecution: true);
        return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        MaybeFail(command, afterExecution: false);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        MaybeFail(command, afterExecution: true);
        return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }
}

// The lost-commit-acknowledgment shape specifically: Postgres COMMITted, and
// the failure is raised from EF's post-commit notification. Not reachable via
// DbCommandInterceptor — Npgsql's COMMIT is a protocol message, not a
// DbCommand.
public sealed class TransientCommitFaultInterceptor : DbTransactionInterceptor
{
    private int _armed;
    private int _commits;

    // Commits that actually reached the server (durable), armed or not.
    public int Commits => Volatile.Read(ref _commits);

    public void ArmOnce()
    {
        Interlocked.Exchange(ref _commits, 0);
        Interlocked.Exchange(ref _armed, 1);
    }

    public void Disarm() => Interlocked.Exchange(ref _armed, 0);

    public override Task TransactionCommittedAsync(
        DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _commits);
        if (Interlocked.CompareExchange(ref _armed, 0, 1) == 1)
            throw TransientFault.Create();
        return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
    }
}
