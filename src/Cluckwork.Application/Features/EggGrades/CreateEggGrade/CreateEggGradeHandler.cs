namespace Cluckwork.Application.Features.EggGrades.CreateEggGrade;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Eggs;

public sealed class CreateEggGradeHandler(
    IEggGradeRepository grades,
    IAuditWriter audit,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser)
{
    public async Task<Result<Guid>> HandleAsync(
        CreateEggGradeCommand command, Guid accountId, CancellationToken ct)
    {
        // Single-farm MVP: grades attach to the seeded farm (same convention as
        // flock creation). Multi-farm picks up a FarmId parameter here.
        var farmId = SeedDefaults.FarmId;

        // #911/#729 — the grade catalog itself is Owner + Manager (#73), but a
        // low-stock floor is farm CONFIGURATION, so only an Owner may put one
        // on a grade. A Manager creating an ordinary grade passes through.
        if (command.LowStockFloor is not null && !EggGradeFloorPolicy.MaySetFloor(currentUser))
            return Result.Failure<Guid>(AppError.Forbidden());

        // Friendly pre-check; the unique index on (account, farm, lower(name))
        // is the real guarantee and races surface as the global 409 mapping.
        if (await grades.NameExistsAsync(farmId, command.Name, excludeId: null, ct))
            return Result.Failure<Guid>(Error.Conflict(
                "EggGrade.DuplicateName", $"A grade named '{command.Name.Trim()}' already exists."));

        var gradeType = Enum.Parse<EggGradeType>(command.GradeType, ignoreCase: true);
        var grade = EggGrade.Create(
            Guid.NewGuid(), accountId, farmId,
            command.Name, gradeType, command.SortOrder, command.IsSaleable,
            lowStockFloor: command.LowStockFloor);

        await grades.AddAsync(grade, ct);
        await audit.WriteAsync(AuditActions.EggGradeCreate, nameof(EggGrade), grade.Id, ct: ct);
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success(grade.Id);
    }
}
