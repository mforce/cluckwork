namespace Cluckwork.Application.Features.EggGrades;

// #853: Egg Operations' grade read port for peer modules and adapters.
public interface IEggGradeLookup
{
    // A missing key is a grade outside the tenant or never created.
    Task<IReadOnlyDictionary<Guid, string>> GetDisplayNamesAsync(
        IReadOnlyCollection<Guid> gradeIds, CancellationToken ct);
}
