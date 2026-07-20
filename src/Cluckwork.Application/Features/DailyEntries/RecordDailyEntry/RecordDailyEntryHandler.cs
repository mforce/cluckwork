namespace Cluckwork.Application.Features.DailyEntries.RecordDailyEntry;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.DailyEntries;
using Cluckwork.Application.Features.EggGrades;
using Cluckwork.Application.Features.Flocks;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Eggs;
using Cluckwork.Domain.Flocks;

public sealed class RecordDailyEntryHandler(
    IDailyEntryRepository repository,
    IEggGradeRepository eggGrades,
    IFlockRepository flocks,
    IFlockScopeGuard flockScope,
    IUnitOfWork unitOfWork)
{
    public async Task<Result<Guid>> HandleAsync(
        RecordDailyEntryCommand command,
        Guid accountId,
        CancellationToken ct)
    {
        // Spec §5.3 (#103): scoped workers may only record for assigned flocks.
        var scope = await flockScope.CheckAsync(command.FlockId, ct);
        if (scope.IsFailure) return Result.Failure<Guid>(scope.Error);

        // Production needs a live flock for the entry's date (#47): archived
        // flocks never accept entries; depleted flocks still accept backfill
        // dated on/before the depletion date (the final laying days are often
        // entered late). The flock must also belong to the farm/house the entry
        // names — ids are caller-supplied and only tenant-checked otherwise.
        var flock = await flocks.GetByIdAsync(command.FlockId, ct);
        if (flock is null)
            return Result.Failure<Guid>(Error.NotFound(nameof(Flock), command.FlockId));
        // Farm only: houses aren't aggregates yet (phantom ids until Phase 2's
        // House model) — add the HouseId match when they are.
        if (flock.FarmId != command.FarmId)
            return Result.Failure<Guid>(Error.Validation(
                "DailyEntry.FlockFarmMismatch",
                $"Flock '{flock.Name}' does not belong to the given farm."));
        if (!flock.CanRecordProductionOn(command.Date))
            return Result.Failure<Guid>(Error.Validation(
                "DailyEntry.FlockNotActive",
                $"Flock '{flock.Name}' is {flock.Status.ToString().ToLowerInvariant()} — production cannot be recorded for {command.Date:yyyy-MM-dd}."));

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
