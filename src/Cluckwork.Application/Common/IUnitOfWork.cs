namespace Cluckwork.Application.Common;

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct = default);
    Task<bool> ExecuteInTransactionAsync(Func<CancellationToken, Task<bool>> operation, CancellationToken ct = default);
}
