namespace Cluckwork.Application.Features.DailyEntries.RecordDailyEntry;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.DailyEntries;
using Cluckwork.Domain.Eggs;

public sealed class RecordDailyEntryHandler(
    IDailyEntryRepository repository,
    IUnitOfWork unitOfWork)
{
    public async Task<Result<Guid>> HandleAsync(
        RecordDailyEntryCommand command,
        Guid accountId,
        CancellationToken ct)
    {
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

        var result = entry.RecordProduction(
            command.TotalEggs, command.CrackedEggs,
            command.DirtyEggs, command.DiscardedEggs,
            command.MortalityCount);

        if (result.IsFailure)
            return Result.Failure<Guid>(result.Error);

        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success(entry.Id);
    }
}
