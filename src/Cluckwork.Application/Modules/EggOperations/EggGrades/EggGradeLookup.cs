using Cluckwork.Application.Modules.EggOperations.Contracts;
using Cluckwork.Domain.Modules.EggOperations.Eggs;

namespace Cluckwork.Application.Modules.EggOperations.EggGrades;

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
