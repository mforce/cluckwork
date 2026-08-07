namespace Cluckwork.Application.Features.EggGrades.UpdateEggGrade;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Common;

public sealed class UpdateEggGradeHandler(
    IEggGradeRepository grades,
    IUnitOfWork unitOfWork,
    IAuditWriter audit)
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

        // Same SaveChanges as the change (#93).
        await audit.WriteAsync(AuditActions.EggGradeUpdate, "EggGrade", grade.Id,
            reason: null, details: new { grade.Name, grade.SortOrder, grade.IsSaleable }, ct: ct);

        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}
