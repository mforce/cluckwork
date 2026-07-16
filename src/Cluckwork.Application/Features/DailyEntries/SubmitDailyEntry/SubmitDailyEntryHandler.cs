namespace Cluckwork.Application.Features.DailyEntries.SubmitDailyEntry;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.EggLots;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Eggs;

// The production -> stock bridge (#8): submitting a daily entry turns its grade
// lines into egg lots, one lot per grade, dated by the entry. Submit + lot
// creation commit atomically; a failed save leaves the entry Draft with no lots.
// Duplicate submits are blocked by the state machine (NotDraft) and, under
// concurrency, by the entry's optimistic Version token — so lots are never
// generated twice.
public sealed class SubmitDailyEntryHandler(
    IDailyEntryRepository entries,
    IEggLotRepository eggLots,
    IUnitOfWork unitOfWork)
{
    public async Task<Result<SubmitDailyEntryResponse>> HandleAsync(
        Guid dailyEntryId, Guid accountId, CancellationToken ct)
    {
        // Tenant query filter scopes the lookup — a foreign entry reads as null.
        var entry = await entries.GetByIdAsync(dailyEntryId, ct);
        if (entry is null)
            return Result.Failure<SubmitDailyEntryResponse>(
                Error.NotFound(nameof(DailyEntry), dailyEntryId));

        var submit = entry.Submit();
        if (submit.IsFailure)
            return Result.Failure<SubmitDailyEntryResponse>(submit.Error);

        var lotIds = new List<Guid>();
        foreach (var line in entry.Grades)
        {
            var lot = EggLot.Create(
                Guid.NewGuid(), accountId, entry.FlockId,
                entry.Date, line.EggGradeId, line.Quantity);
            await eggLots.AddAsync(lot, ct);
            lotIds.Add(lot.Id);
        }

        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success(new SubmitDailyEntryResponse(entry.Id, entry.Status.ToString(), lotIds));
    }
}

public sealed record SubmitDailyEntryResponse(Guid Id, string Status, IReadOnlyList<Guid> EggLotIds);
