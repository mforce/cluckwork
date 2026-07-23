namespace Cluckwork.Infrastructure.Persistence;

using Cluckwork.Application.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

public sealed class UnitOfWork(AppDbContext db) : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);

    public async Task<bool> ExecuteInTransactionAsync(
        Func<CancellationToken, Task<bool>> operation, CancellationToken ct = default)
    {
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(ct);
        var shouldCommit = await operation(ct);
        if (!shouldCommit)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }
}
