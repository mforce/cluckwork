namespace Cluckwork.Application.Features.EggGrades;

public sealed class EggGradeLookup(IEggGradeRepository grades) : IEggGradeLookup
{
    public Task<IReadOnlyDictionary<Guid, string>> GetDisplayNamesAsync(
        IReadOnlyCollection<Guid> gradeIds, CancellationToken ct) =>
        grades.GetDisplayNamesAsync(gradeIds, ct);
}
