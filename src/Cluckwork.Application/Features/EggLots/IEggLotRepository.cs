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
}
