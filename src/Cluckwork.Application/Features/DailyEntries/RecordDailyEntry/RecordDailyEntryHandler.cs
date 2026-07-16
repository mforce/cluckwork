namespace Cluckwork.Application.Features.DailyEntries.RecordDailyEntry;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.DailyEntries;
using Cluckwork.Application.Features.EggGrades;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Eggs;

public sealed class RecordDailyEntryHandler(
    IDailyEntryRepository repository,
    IEggGradeRepository eggGrades,
    IUnitOfWork unitOfWork)
{
    public async Task<Result<Guid>> HandleAsync(
        RecordDailyEntryCommand command,
        Guid accountId,
        CancellationToken ct)
    {
        // Grade ids must be the tenant's own, belong to the entry's farm, and be
        // active + saleable — grade lines capture sellable production; non-saleable
        // buckets (cracked/dirty/...) are the entry's loss counts. The tenant query
        // filter scopes the lookup; grades are farm-scoped (spec §9.1).
        if (command.Grades is { Count: > 0 })
        {
            var known = (await eggGrades.ListActiveAsync(command.FarmId, ct))
                .Where(g => g.IsSaleable)
                .Select(g => g.Id)
                .ToHashSet();

            var unknown = command.Grades.Where(g => !known.Contains(g.EggGradeId)).ToList();
            if (unknown.Count > 0)
                return Result.Failure<Guid>(Error.Validation(
                    "DailyEntry.UnknownGrade",
                    "One or more egg grades do not exist, are inactive, or are not saleable."));
        }

        var existing = await repository.FindByNaturalKeyAsync(
            accountId, command.FarmId, command.HouseId, command.FlockId, command.Date, ct);

        DailyEntry entry;
        if (existing is null)
        {
            entry = DailyEntry.Create(
                Guid.NewGuid(), accountId,
                command.FarmId, command.HouseId, command.FlockId, command.Date);

            await repository.AddAsync(entry, ct);
        }
        else
        {
            entry = existing;
        }

        var grades = command.Grades?
            .Select(g => new GradeQuantity(g.EggGradeId, g.Quantity))
            .ToList();

        var result = entry.RecordProduction(
            command.TotalEggs, command.CrackedEggs,
            command.DirtyEggs, command.DiscardedEggs,
            command.MortalityCount, grades);

        if (result.IsFailure)
            return Result.Failure<Guid>(result.Error);

        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success(entry.Id);
    }
}
