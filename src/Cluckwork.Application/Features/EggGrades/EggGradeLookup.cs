using Cluckwork.Application.Features.Eggs;
using Cluckwork.Domain.Eggs;

namespace Cluckwork.Application.Features.EggGrades;

public sealed class EggGradeLookup(IEggGradeRepository grades) : IEggGradeLookup
{
    public Task<IReadOnlyDictionary<Guid, string>> GetDisplayNamesAsync(
        IReadOnlyCollection<Guid> gradeIds, CancellationToken ct) =>
        grades.GetDisplayNamesAsync(gradeIds, ct);

    public async Task<EggGradeDetails?> GetAsync(Guid id, CancellationToken ct) =>
        await grades.GetByIdAsync(id, ct) is { } grade ? ToDetails(grade) : null;

    internal static EggGradeDetails ToDetails(EggGrade g) =>
        new(g.Id, g.FarmId, g.Name, g.GradeType, g.SortOrder, g.IsSaleable,
            g.DailyEntryKind, g.Active, g.LowStockFloor);
}
