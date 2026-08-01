namespace Cluckwork.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

// #307 — IdempotencyMiddleware wraps a whole write request in one transaction
// on the request's scoped AppDbContext, so the domain mutation and the
// idempotency completion record commit (or roll back) as ONE atomic unit —
// closing the "a stolen lease presumes the prior attempt dead, but it may
// have already committed" trap by construction: a crash before that single
// commit leaves nothing durable at all.
//
// Every OTHER place that begins its own transaction on a scoped AppDbContext
// (IUnitOfWork.ExecuteInTransactionAsync, IdentityProvider's user/password
// flows) must therefore be reentrant: join the ambient transaction instead of
// nesting a second Database.BeginTransactionAsync (EF Core throws
// InvalidOperationException — "a transaction is already in progress") or
// committing/rolling back a transaction it does not own. Outside a request
// the idempotency middleware wraps (unit tests calling a handler directly, a
// CLI command, a read), Database.CurrentTransaction is null and this behaves
// exactly like a plain BeginTransactionAsync.
//
// #269 — EnableRetryOnFailure adds a second constraint on top of the one
// above: a transaction opened OUTSIDE database.CreateExecutionStrategy()
// .ExecuteAsync makes EF throw InvalidOperationException the moment
// anything else touches the DbContext while it's open — unconditionally, not
// just on an actual transient failure. So the "owned" case (no ambient
// transaction to join — THIS call is the one opening it) can no longer just
// call BeginTransactionAsync and hand back a scope for the caller to drive
// at its own pace; it has to run the caller's ENTIRE unit of work inside the
// execution strategy. RunAsync below is the one place that decides which case
// applies; every caller passes its whole transactional unit as `work` rather
// than holding the scope open itself.
//
// The owned unit runs through SingleAttemptExecution — inside the strategy,
// but never replayed. `work` is caller-supplied and routinely NOT replayable:
// a failed attempt leaves its entities tracked as Added on this same scoped
// AppDbContext (EF does not detach them, so a retry flushes the failed
// attempt's rows alongside the fresh ones — duplicate users on the unique
// email index, duplicate audit rows, a refresh token nobody was ever issued),
// and for `bootstrap-admin` the unit sits inside a SESSION-scoped
// pg_advisory_lock that a reconnect drops without the retry reacquiring it or
// re-checking for an Owner. See SingleAttemptExecution for the full rationale
// and for what retrying still covers.
public static class AmbientTransaction
{
    public static async Task<T> RunAsync<T>(
        DatabaseFacade database,
        Func<IAmbientTransactionScope, CancellationToken, Task<T>> work,
        CancellationToken ct = default)
    {
        if (database.CurrentTransaction is not null)
            return await work(JoinedTransactionScope.Instance, ct);

        return await SingleAttemptExecution.RunAsync(database, async () =>
        {
            var transaction = await database.BeginTransactionAsync(ct);
            await using var scope = new OwnedTransactionScope(transaction);
            return await work(scope, ct);
        });
    }
}

// Commit/Rollback/Dispose all behave like IDbContextTransaction when this
// caller is the actual owner, and are all no-ops when it merely joined an
// ambient transaction someone else began — that owner alone decides the
// outcome.
public interface IAmbientTransactionScope : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct = default);
    Task RollbackAsync(CancellationToken ct = default);
}

internal sealed class OwnedTransactionScope(IDbContextTransaction transaction) : IAmbientTransactionScope
{
    public Task CommitAsync(CancellationToken ct = default) => transaction.CommitAsync(ct);
    public Task RollbackAsync(CancellationToken ct = default) => transaction.RollbackAsync(ct);
    public ValueTask DisposeAsync() => transaction.DisposeAsync();
}

internal sealed class JoinedTransactionScope : IAmbientTransactionScope
{
    public static readonly JoinedTransactionScope Instance = new();
    private JoinedTransactionScope() { }
    public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task RollbackAsync(CancellationToken ct = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
