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
        // #269 — for a REAL HTTP write request this is always the joined
        // case: every feature handler reaches this through a POST/PUT/PATCH/
        // DELETE endpoint IdempotencyMiddleware already wrapped, so `work`
        // below runs exactly once. The "owned" (no ambient transaction)
        // branch only fires for a caller that invokes a handler directly,
        // outside HTTP (a unit/integration test, or a future non-HTTP
        // caller) — there, a transient failure reruns `operation` from
        // scratch against a fresh transaction. That is safe for this
        // codebase's handlers specifically because they are pure
        // domain+EF (no non-DB side effects to double), but `operation` is
        // caller-supplied and this class has no way to enforce that in
        // general — a future handler with an external side effect inside
        // `operation` would need its own idempotency guard before relying on
        // this owned/retried path.
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
