namespace Cluckwork.Infrastructure.Repositories;

using Cluckwork.Application.Features.EggGrades;
using Cluckwork.Domain.Eggs;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed class EggGradeRepository(AppDbContext db) : IEggGradeRepository
{
    // Reads rely on the tenant query filter (AccountId == current tenant).
    public Task<EggGrade?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.EggGrades.FirstOrDefaultAsync(g => g.Id == id, ct);

    public async Task<IReadOnlyList<EggGrade>> ListActiveAsync(Guid? farmId = null, CancellationToken ct = default) =>
        await db.EggGrades
            .AsNoTracking()
            .Where(g => g.Active && (farmId == null || g.FarmId == farmId))
            .OrderBy(g => g.SortOrder).ThenBy(g => g.Name)
            .ToListAsync(ct);

    public async Task AddAsync(EggGrade entity, CancellationToken ct = default) =>
        await db.EggGrades.AddAsync(entity, ct);

    public void Update(EggGrade entity) => db.EggGrades.Update(entity);

    public void Remove(EggGrade entity) => db.EggGrades.Remove(entity);
}
