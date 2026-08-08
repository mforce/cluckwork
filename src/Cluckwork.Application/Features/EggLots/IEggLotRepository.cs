namespace Cluckwork.Application.Features.EggLots;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Eggs;

public interface IEggLotRepository : IRepository<EggLot, Guid>
{
    // Returns FIFO-ordered available lots for the account across ALL requested
    // grades in ONE statement, acquired with a pessimistic FOR UPDATE lock.
    // Must be called inside an open transaction. Single-statement, canonical
    // (ProductionDate, Id) ordering is load-bearing: acquiring locks per grade
    // in order-line order deadlocks against a void locking the same lots.
    Task<IReadOnlyList<EggLot>> GetAvailableFifoLockedAsync(
        Guid accountId, IReadOnlyList<Guid> eggGradeIds, DateOnly allocationDate,
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

    // FOR UPDATE lock on ALL lots a daily entry's submit generated — the
    // entry adjust/void reconciliation path (#69). Includes emptied and
    // restricted lots (reconciliation must see every lot the entry produced);
    // same canonical (ProductionDate, Id) ordering as the other locking paths.
    Task<IReadOnlyList<EggLot>> GetByDailyEntryLockedAsync(
        Guid accountId, Guid dailyEntryId, CancellationToken ct = default);

    // Read-only lot listing for the stock drill-down (#101), newest production
    // first, optionally filtered by grade and/or an inclusive production-date
    // window (#465). Capped by limit — the SPA pages.
    Task<IReadOnlyList<EggLot>> ListAsync(
        Guid? eggGradeId, DateOnly? from, DateOnly? to,
        int limit, int offset, CancellationToken ct = default);
}

public sealed record StockByGrade(
    Guid EggGradeId, string GradeName, int SortOrder, int Available, int Restricted);
