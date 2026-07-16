namespace Cluckwork.Application.Features.EggGrades.CreateEggGrade;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Eggs;

public sealed class CreateEggGradeHandler(
    IEggGradeRepository grades,
    IUnitOfWork unitOfWork)
{
    public async Task<Result<Guid>> HandleAsync(
        CreateEggGradeCommand command, Guid accountId, CancellationToken ct)
    {
        // Single-farm MVP: grades attach to the seeded farm (same convention as
        // flock creation). Multi-farm picks up a FarmId parameter here.
        var farmId = SeedDefaults.FarmId;

        // Friendly pre-check; the unique index on (account, farm, lower(name))
        // is the real guarantee and races surface as the global 409 mapping.
        if (await grades.NameExistsAsync(farmId, command.Name, excludeId: null, ct))
            return Result.Failure<Guid>(Error.Conflict(
                "EggGrade.DuplicateName", $"A grade named '{command.Name.Trim()}' already exists."));

        var gradeType = Enum.Parse<EggGradeType>(command.GradeType, ignoreCase: true);
        var grade = EggGrade.Create(
            Guid.NewGuid(), accountId, farmId,
            command.Name, gradeType, command.SortOrder, command.IsSaleable);

        await grades.AddAsync(grade, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success(grade.Id);
    }
}
