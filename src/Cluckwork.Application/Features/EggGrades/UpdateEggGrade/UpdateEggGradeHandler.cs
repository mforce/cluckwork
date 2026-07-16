namespace Cluckwork.Application.Features.EggGrades.UpdateEggGrade;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Common;

public sealed class UpdateEggGradeHandler(
    IEggGradeRepository grades,
    IUnitOfWork unitOfWork)
{
    public async Task<Result> HandleAsync(UpdateEggGradeCommand command, CancellationToken ct)
    {
        var grade = await grades.GetByIdAsync(command.EggGradeId, ct);
        if (grade is null)
            return Result.Failure(Error.NotFound("EggGrade", command.EggGradeId));

        if (await grades.NameExistsAsync(grade.FarmId, command.Name, excludeId: grade.Id, ct))
            return Result.Failure(Error.Conflict(
                "EggGrade.DuplicateName", $"A grade named '{command.Name.Trim()}' already exists."));

        var result = grade.Update(command.Name, command.SortOrder, command.IsSaleable);
        if (result.IsFailure) return result;

        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}
