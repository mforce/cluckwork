namespace Cluckwork.Application.Features.Sales;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Sales;

public interface ISalesOrderRepository : IRepository<SalesOrder, Guid> { }
