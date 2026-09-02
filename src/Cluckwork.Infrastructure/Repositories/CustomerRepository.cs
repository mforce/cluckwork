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

    // #512 — discovery. Same shape as the flock query: the literal search is a
    // WHERE clause ahead of the ORDER BY, and `Id` breaks name ties so paging
    // over duplicate names neither repeats nor skips a row. Tenant isolation
    // remains the structural global filter (#613).
    public async Task<IReadOnlyList<Customer>> SearchAsync(
        string? search, int limit, int offset, CancellationToken ct = default)
    {
        var query = db.Customers.AsNoTracking();

        var trimmed = LiteralSearch.Normalize(search);
        if (trimmed is not null)
        {
            var pattern = LiteralSearch.ContainsPattern(trimmed);
            query = query.Where(c => EF.Functions.ILike(c.Name, pattern, LiteralSearch.EscapeChar));
        }

        return await query
            .OrderBy(c => c.Name).ThenBy(c => c.Id)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);
    }

    // #512 US4 — scoped bulk customer display names for row projections (#512
    // T048). LINQ over the filtered DbSet, so the tenant filter composes and an
    // IgnoreQueryFilters() mutation stays observable (#613). One read per page,
    // ids bounded to that page; a missing key means outside-tenant or gone.
    public async Task<IReadOnlyDictionary<Guid, CustomerReference>> GetDisplayNamesAsync(
        IReadOnlyCollection<Guid> customerIds, CancellationToken ct = default)
    {
        if (customerIds.Count == 0) return new Dictionary<Guid, CustomerReference>();

        var ids = customerIds.Distinct().ToArray();
        var rows = await db.Customers
            .AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .Select(c => new CustomerReference(c.Id, c.Name))
            .TagWith(ReferenceMarkers.CustomerReference)
            .ToListAsync(ct);

        var map = new Dictionary<Guid, CustomerReference>(rows.Count);
        foreach (var r in rows)
            map[r.Id] = r;
        return map;
    }

    public async Task AddAsync(Customer entity, CancellationToken ct = default) =>
        await db.Customers.AddAsync(entity, ct);

    public void Update(Customer entity) => db.Customers.Update(entity);

    public void Remove(Customer entity) => db.Customers.Remove(entity);
}
