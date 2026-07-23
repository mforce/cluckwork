namespace Cluckwork.Application.Common;

using System.Data;

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct = default);
    Task<bool> ExecuteInTransactionAsync(Func<CancellationToken, Task<bool>> operation, CancellationToken ct = default);

    // Same, at a chosen isolation level. Needed where something is READ and
    // then acted on in the same breath, and the read must not be able to go
    // stale before the write lands — §4.6's currency lock reads "this farm has
    // no money rows" and then writes a new currency on the strength of it
    // (UpdateFarmSettingsHandler).
    Task<bool> ExecuteInTransactionAsync(
        Func<CancellationToken, Task<bool>> operation,
        IsolationLevel isolationLevel,
        CancellationToken ct = default);
}
