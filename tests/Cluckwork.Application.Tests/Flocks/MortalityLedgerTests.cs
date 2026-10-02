using Cluckwork.Application.Features.Flocks;
using Cluckwork.Domain.Flocks;

namespace Cluckwork.Application.Tests.Flocks;

// #852: the ledger port picks the movement type from the sign and only adds
// the row; committing it is the caller's unit of work.
public sealed class MortalityLedgerTests
{
    private static readonly Guid AccountId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid FlockId = Guid.Parse("00000000-0000-0000-0000-0000000000f1");
    private static readonly Guid EntryId = Guid.Parse("00000000-0000-0000-0000-0000000000e1");
    private static readonly DateOnly Date = new(2026, 9, 30);

    private static async Task<BirdMovement> AppendAsync(int birdsRemoved, string note)
    {
        var movements = new CapturingMovements();
        await new MortalityLedger(movements).AppendAsync(AccountId, FlockId, Date, birdsRemoved, EntryId, note, default);
        return Assert.Single(movements.Added);
    }

    [Fact]
    public async Task PositiveCount_IsMortalityTiedToTheEntry()
    {
        var row = await AppendAsync(4, "Daily entry mortality");
        Assert.Equal(
            (AccountId, FlockId, Date, BirdMovementType.Mortality, 4, "Daily entry mortality", (Guid?)EntryId),
            (row.AccountId, row.FlockId, row.Date, row.Type, row.Quantity, row.Note, row.DailyEntryId));
    }

    [Fact]
    public async Task NegativeCount_IsAnAdjustmentThatPutsBirdsBack()
    {
        var row = await AppendAsync(-3, "Entry voided: miscount");
        Assert.Equal((BirdMovementType.Adjustment, -3), (row.Type, row.Quantity));
    }

    [Fact]
    public async Task OverLongNote_IsTruncatedToTheLedgerLimit()
    {
        var row = await AppendAsync(1, "Entry adjusted: " + new string('x', BirdMovement.MaxNoteLength));
        Assert.Equal(("Entry adjusted: " + new string('x', BirdMovement.MaxNoteLength))[..BirdMovement.MaxNoteLength], row.Note);
    }

    private sealed class CapturingMovements : IBirdMovementRepository
    {
        public List<BirdMovement> Added { get; } = [];

        public Task AddAsync(BirdMovement entity, CancellationToken ct = default)
        {
            Added.Add(entity);
            return Task.CompletedTask;
        }

        public Task<BirdMovement?> GetByIdAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BirdMovement>> ListByFlockAsync(Guid flockId, int limit, int offset, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<Dictionary<Guid, long>> RemovedForFlocksAsync(IReadOnlyCollection<Guid> flockIds, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<long> RemovedForFlockAsync(Guid flockId, CancellationToken ct = default) => throw new NotSupportedException();
        public void Update(BirdMovement entity) => throw new NotSupportedException();
        public void Remove(BirdMovement entity) => throw new NotSupportedException();
    }
}
