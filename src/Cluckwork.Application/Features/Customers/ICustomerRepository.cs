namespace Cluckwork.Application.Features.Customers;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Sales;

public interface ICustomerRepository : IRepository<Customer, Guid>
{
    Task<IReadOnlyList<Customer>> ListAsync(int limit, int offset, CancellationToken ct = default);
}
