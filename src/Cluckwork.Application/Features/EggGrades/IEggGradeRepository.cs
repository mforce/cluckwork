namespace Cluckwork.Application.Features.EggGrades;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Eggs;

public interface IEggGradeRepository : IRepository<EggGrade, Guid>
{
    // Active grades for the current tenant, saleable and not, in sort order.
    // Pass farmId to filter server-side (grades are farm-scoped, spec §9.1).
    Task<IReadOnlyList<EggGrade>> ListActiveAsync(Guid? farmId = null, CancellationToken ct = default);

    // Management view: every grade of the tenant, inactive included.
    Task<IReadOnlyList<EggGrade>> ListAllAsync(CancellationToken ct = default);

    // Case-insensitive duplicate check within a farm; excludeId skips the grade
    // being renamed.
    Task<bool> NameExistsAsync(Guid farmId, string name, Guid? excludeId = null, CancellationToken ct = default);
}
