namespace Cluckwork.Application.Features.EggGrades;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Eggs;

public interface IEggGradeRepository : IRepository<EggGrade, Guid>
{
    // Active grades for the current tenant, saleable and not, in sort order.
    Task<IReadOnlyList<EggGrade>> ListActiveAsync(CancellationToken ct = default);
}
