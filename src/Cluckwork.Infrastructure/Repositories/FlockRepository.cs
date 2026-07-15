namespace Cluckwork.Infrastructure.Repositories;

using Cluckwork.Application.Features.Flocks;
using Cluckwork.Domain.Flocks;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed class FlockRepository(AppDbContext db) : IFlockRepository
{
    // Reads rely on the tenant query filter (AccountId == current tenant), so the
    // caller only ever sees its own flocks.
    // Tracked: DepleteFlockHandler mutates the returned entity.
    public Task<Flock?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.Flocks.FirstOrDefaultAsync(f => f.Id == id, ct);

    // Read-only, paged.
    public async Task<IReadOnlyList<Flock>> ListAsync(int limit, int offset, CancellationToken ct = default) =>
        await db.Flocks
            .AsNoTracking()
            .OrderBy(f => f.Name)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

    public async Task AddAsync(Flock entity, CancellationToken ct = default) =>
        await db.Flocks.AddAsync(entity, ct);

    public void Update(Flock entity) => db.Flocks.Update(entity);

    public void Remove(Flock entity) => db.Flocks.Remove(entity);
}
