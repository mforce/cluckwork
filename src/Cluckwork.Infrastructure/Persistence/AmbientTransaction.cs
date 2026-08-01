namespace Cluckwork.Infrastructure.Persistence;

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
public static class AmbientTransaction
{
    public static async Task<IAmbientTransactionScope> BeginAsync(
        DatabaseFacade database, CancellationToken ct = default)
    {
        if (database.CurrentTransaction is not null)
            return JoinedTransactionScope.Instance;

        var transaction = await database.BeginTransactionAsync(ct);
        return new OwnedTransactionScope(transaction);
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
