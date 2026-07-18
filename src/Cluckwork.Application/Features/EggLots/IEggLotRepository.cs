namespace Cluckwork.Application.Features.EggLots;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Eggs;

public interface IEggLotRepository : IRepository<EggLot, Guid>
{
    // Returns FIFO-ordered available lots for the account + grade, acquired with
    // a pessimistic FOR UPDATE lock. Must be called inside an open transaction.
    Task<IReadOnlyList<EggLot>> GetAvailableFifoLockedAsync(
        Guid accountId, Guid eggGradeId, DateOnly allocationDate,
        CancellationToken ct = default);

    // Current stock aggregated by grade for the tenant. Available excludes lots
    // under withdrawal restriction as of the given date; those sum separately.
    Task<IReadOnlyList<StockByGrade>> GetStockByGradeAsync(
        DateOnly asOfDate, CancellationToken ct = default);

    // FOR UPDATE lock on specific lots for the void-restore path (#60). Same
    // FIFO ordering as GetAvailableFifoLockedAsync so a void and a confirm
    // touching the same lots acquire locks in a consistent order.
    Task<IReadOnlyList<EggLot>> GetByIdsLockedAsync(
        Guid accountId, IReadOnlyList<Guid> lotIds, CancellationToken ct = default);
}

public sealed record StockByGrade(
    Guid EggGradeId, string GradeName, int SortOrder, int Available, int Restricted);
