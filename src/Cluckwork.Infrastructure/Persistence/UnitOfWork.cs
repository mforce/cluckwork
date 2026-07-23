namespace Cluckwork.Infrastructure.Persistence;

using System.Data;
using Cluckwork.Application.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

public sealed class UnitOfWork(AppDbContext db) : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);

    public Task<bool> ExecuteInTransactionAsync(
        Func<CancellationToken, Task<bool>> operation, CancellationToken ct = default) =>
        RunAsync(operation, token => db.Database.BeginTransactionAsync(token), ct);

    public Task<bool> ExecuteInTransactionAsync(
        Func<CancellationToken, Task<bool>> operation, IsolationLevel isolationLevel, CancellationToken ct = default) =>
        RunAsync(operation, token => db.Database.BeginTransactionAsync(isolationLevel, token), ct);

    private async Task<bool> RunAsync(
        Func<CancellationToken, Task<bool>> operation,
        Func<CancellationToken, Task<IDbContextTransaction>> begin,
        CancellationToken ct)
    {
        await using IDbContextTransaction transaction = await begin(ct);
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
