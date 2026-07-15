namespace Cluckwork.Infrastructure.Repositories;

using Cluckwork.Application.Features.Flocks;
using Cluckwork.Domain.Flocks;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed class FlockRepository(AppDbContext db) : IFlockRepository
{
    // Reads rely on the tenant query filter (AccountId == current tenant), so the
    // caller only ever sees its own flocks.
    public Task<Flock?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.Flocks.FirstOrDefaultAsync(f => f.Id == id, ct);

    public async Task<IReadOnlyList<Flock>> ListAsync(CancellationToken ct = default) =>
        await db.Flocks.OrderBy(f => f.Name).ToListAsync(ct);

    public async Task AddAsync(Flock entity, CancellationToken ct = default) =>
        await db.Flocks.AddAsync(entity, ct);

    public void Update(Flock entity) => db.Flocks.Update(entity);

    public void Remove(Flock entity) => db.Flocks.Remove(entity);
}
