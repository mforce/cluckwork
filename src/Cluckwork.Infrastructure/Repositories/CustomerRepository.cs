namespace Cluckwork.Infrastructure.Repositories;

using Cluckwork.Application.Features.Customers;
using Cluckwork.Domain.Sales;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed class CustomerRepository(AppDbContext db) : ICustomerRepository
{
    // Reads rely on the tenant query filter (AccountId == current tenant).
    public Task<Customer?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.Customers.FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task<IReadOnlyList<Customer>> ListAsync(int limit, int offset, CancellationToken ct = default) =>
        await db.Customers
            .AsNoTracking()
            .OrderBy(c => c.Name).ThenBy(c => c.Id)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

    public async Task AddAsync(Customer entity, CancellationToken ct = default) =>
        await db.Customers.AddAsync(entity, ct);

    public void Update(Customer entity) => db.Customers.Update(entity);

    public void Remove(Customer entity) => db.Customers.Remove(entity);
}
