using Cluckwork.Application.Modules.FlockManagement.Contracts;
using Cluckwork.Domain.Modules.FlockManagement.Contracts;
using Cluckwork.Domain.Modules.FlockManagement.Flocks;

namespace Cluckwork.Application.Modules.FlockManagement.Flocks;

public sealed class MortalityLedger(IBirdMovementRepository movements) : IMortalityLedger
{
    public Task AppendAsync(
        Guid accountId, Guid flockId, DateOnly date, int birdsRemoved, Guid dailyEntryId, string note,
        CancellationToken ct) =>
        movements.AddAsync(BirdMovement.Create(
            Guid.NewGuid(), accountId, flockId, date,
            birdsRemoved > 0 ? BirdMovementType.Mortality : BirdMovementType.Adjustment,
            birdsRemoved,
            note: note.Length <= BirdMovement.MaxNoteLength ? note : note[..BirdMovement.MaxNoteLength],
            dailyEntryId: dailyEntryId), ct);
}
