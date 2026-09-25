namespace Cluckwork.Application.Features.EggGrades.UpdateEggGrade;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Common;

public sealed class UpdateEggGradeHandler(
    IEggGradeRepository grades,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    ICurrentUser currentUser)
{
    public async Task<Result> HandleAsync(UpdateEggGradeCommand command, CancellationToken ct)
    {
        var grade = await grades.GetByIdAsync(command.EggGradeId, ct);
        if (grade is null)
            return Result.Failure(Error.NotFound("EggGrade", command.EggGradeId));

        // #911/#729 — a Manager keeps every other field on this screen; only
        // the floor is Owner-only, so the refusal is conditional on the floor
        // actually MOVING. A Manager saving the dialog with the floor it
        // already has changes no configuration and is allowed through.
        if (command.LowStockFloor != grade.LowStockFloor
            && !EggGradeFloorPolicy.MaySetFloor(currentUser))
            return Result.Failure(AppError.Forbidden());

        if (await grades.NameExistsAsync(grade.FarmId, command.Name, excludeId: grade.Id, ct))
            return Result.Failure(Error.Conflict(
                "EggGrade.DuplicateName", $"A grade named '{command.Name.Trim()}' already exists."));

        var result = grade.Update(
            command.Name, command.SortOrder, command.IsSaleable, command.LowStockFloor);
        if (result.IsFailure) return result;

        // Same SaveChanges as the change (#93).
        await audit.WriteAsync(AuditActions.EggGradeUpdate, "EggGrade", grade.Id,
            reason: null, details: new { grade.Name, grade.SortOrder, grade.IsSaleable, grade.LowStockFloor }, ct: ct);

        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}
