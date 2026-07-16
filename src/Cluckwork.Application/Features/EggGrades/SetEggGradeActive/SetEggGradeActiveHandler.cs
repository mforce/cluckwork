namespace Cluckwork.Application.Features.EggGrades.SetEggGradeActive;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Common;

// Backs both POST /egg-grades/{id}/activate and /deactivate. Deactivation only
// stops NEW grading/order lines (capture flows filter to active grades); live
// stock in the grade keeps selling until the lots drain, and historical rows
// keep resolving the grade's name.
public sealed class SetEggGradeActiveHandler(
    IEggGradeRepository grades,
    IUnitOfWork unitOfWork)
{
    public async Task<Result> HandleAsync(Guid eggGradeId, bool active, CancellationToken ct)
    {
        var grade = await grades.GetByIdAsync(eggGradeId, ct);
        if (grade is null)
            return Result.Failure(Error.NotFound("EggGrade", eggGradeId));

        var result = active ? grade.Activate() : grade.Deactivate();
        if (result.IsFailure) return result;

        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}
