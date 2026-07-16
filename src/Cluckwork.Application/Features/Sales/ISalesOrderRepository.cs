namespace Cluckwork.Application.Features.Sales;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Sales;

public interface ISalesOrderRepository : IRepository<SalesOrder, Guid>
{
    // Paged, newest-first, items included. Optional status/customer filters.
    Task<IReadOnlyList<SalesOrder>> ListAsync(
        SalesOrderStatus? status, Guid? customerId, int limit, int offset,
        CancellationToken ct = default);
}
