namespace Cluckwork.Infrastructure.Persistence;

using Cluckwork.Application.Common;

public sealed class UnitOfWork(AppDbContext db) : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);

    public Task<bool> ExecuteInTransactionAsync(
        Func<CancellationToken, Task<bool>> operation, CancellationToken ct = default) =>
        // #307 — AmbientTransaction.RunAsync joins IdempotencyMiddleware's
        // request-wide transaction when one is already open on this SAME
        // scoped AppDbContext (see its doc comment), instead of nesting a
        // second BeginTransactionAsync or committing/rolling back a
        // transaction this call doesn't own.
        //
        // #269 — the delegate shape is what EnableRetryOnFailure forces (a
        // user-initiated transaction must be opened inside an execution
        // strategy); it is not a retry. `operation` runs exactly once on
        // BOTH branches — joined (the normal HTTP write, already inside
        // IdempotencyMiddleware's transaction) and owned (a caller invoking a
        // handler directly, outside HTTP). `operation` is caller-supplied and
        // this class cannot know whether it is replayable, so it never
        // replays it; see SingleAttemptExecution.
        AmbientTransaction.RunAsync(db.Database, async (scope, token) =>
        {
            var shouldCommit = await operation(token);
            if (!shouldCommit)
            {
                await scope.RollbackAsync(token);
                return false;
            }

            await db.SaveChangesAsync(token);
            await scope.CommitAsync(token);
            return true;
        }, ct);
}
