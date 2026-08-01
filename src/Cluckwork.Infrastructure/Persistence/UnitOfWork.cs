namespace Cluckwork.Infrastructure.Persistence;

using Cluckwork.Application.Common;

public sealed class UnitOfWork(AppDbContext db) : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);

    public async Task<bool> ExecuteInTransactionAsync(
        Func<CancellationToken, Task<bool>> operation, CancellationToken ct = default)
    {
        // #307 — AmbientTransaction.BeginAsync joins IdempotencyMiddleware's
        // request-wide transaction when one is already open on this SAME
        // scoped AppDbContext (see its doc comment), instead of nesting a
        // second BeginTransactionAsync or committing/rolling back a
        // transaction this call doesn't own.
        await using var scope = await AmbientTransaction.BeginAsync(db.Database, ct);
        var shouldCommit = await operation(ct);
        if (!shouldCommit)
        {
            await scope.RollbackAsync(ct);
            return false;
        }

        await db.SaveChangesAsync(ct);
        await scope.CommitAsync(ct);
        return true;
    }
}
