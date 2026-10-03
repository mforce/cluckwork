using System.Reflection;
using Cluckwork.Application.Features.DailyEntries;
using Cluckwork.Application.Features.DailyEntries.RecordDailyEntry;
using Cluckwork.Application.Features.EggGrades;
using Cluckwork.Application.Features.EggLots;
using Cluckwork.Application.Features.Eggs;
using Cluckwork.Domain.Eggs;

namespace Cluckwork.Application.Tests.Eggs;

// #853: the read paths copy each aggregate field by field into positional
// records, where two Guids, two counts or two dates can swap without a
// compile error.
public sealed class EggOperationsModuleTests
{
    private static readonly Guid AccountId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid FarmId = Guid.Parse("00000000-0000-0000-0000-0000000000f1");
    private static readonly Guid HouseId = Guid.Parse("00000000-0000-0000-0000-0000000000f2");
    private static readonly Guid FlockId = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid AdjustedEntryId = Guid.Parse("00000000-0000-0000-0000-0000000000e1");
    private static readonly Guid VoidedEntryId = Guid.Parse("00000000-0000-0000-0000-0000000000e2");
    private static readonly Guid LargeId = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    private static readonly Guid MediumId = Guid.Parse("00000000-0000-0000-0000-0000000000c2");
    private static readonly Guid CrackedId = Guid.Parse("00000000-0000-0000-0000-0000000000c3");
    private static readonly Guid DirtyId = Guid.Parse("00000000-0000-0000-0000-0000000000c4");
    private static readonly Guid LotId = Guid.Parse("00000000-0000-0000-0000-0000000000d1");
    private static readonly Guid MovementId = Guid.Parse("00000000-0000-0000-0000-0000000000d2");
    private static readonly Guid OrderId = Guid.Parse("00000000-0000-0000-0000-0000000000d3");
    private static readonly DateOnly Day = new(2026, 9, 20);
    private static readonly DateTimeOffset LockedAt = new(2026, 9, 28, 1, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset MovedAt = new(2026, 9, 21, 8, 30, 0, TimeSpan.Zero);

    private readonly FakeEntries _entries = new();
    private readonly FakeGrades _grades = new();
    private readonly FakeLots _lots = new();
    private readonly FakeMovements _movements = new();

    // The handlers sit on the write paths only; these tests exercise the reads.
    private EggOperationsModule Module() => new(
        _entries, _grades, _lots, _movements,
        null!, null!, null!, null!, null!, null!, null!, null!, null!);

    public EggOperationsModuleTests()
    {
        var adjusted = DailyEntry.Create(AdjustedEntryId, AccountId, FarmId, HouseId, FlockId, Day);
        Assert.True(adjusted.RecordProduction(100, 4, 3, 2, 1,
            [new GradeQuantity(LargeId, 60), new GradeQuantity(MediumId, 31)]).IsSuccess);
        Assert.True(adjusted.Submit(CrackedId, DirtyId).IsSuccess);
        Assert.True(adjusted.Lock(LockedAt).IsSuccess);
        Assert.True(adjusted.ManagerAdjust(102, 5, 3, 2, 7, "recount",
            [new GradeQuantity(LargeId, 62), new GradeQuantity(MediumId, 30)]).IsSuccess);
        _entries.Rows.Add(adjusted);

        var voided = DailyEntry.Create(VoidedEntryId, AccountId, FarmId, HouseId, FlockId, Day.AddDays(-1));
        Assert.True(voided.RecordProduction(10, 0, 0, 0, 0, [new GradeQuantity(LargeId, 10)]).IsSuccess);
        Assert.True(voided.Submit().IsSuccess);
        Assert.True(voided.Void("wrong flock").IsSuccess);
        _entries.Rows.Add(voided);

        var cracked = EggGrade.Create(
            CrackedId, AccountId, FarmId, "Cracked", EggGradeType.Quality, 7, isSaleable: true,
            DailyEntryKind.Cracked, lowStockFloor: 40);
        Assert.True(cracked.Deactivate().IsSuccess);
        _grades.Rows.Add(cracked);

        var lot = EggLot.Create(LotId, AccountId, FlockId, Day, LargeId, 60, dailyEntryId: AdjustedEntryId);
        Assert.True(lot.Allocate(25, Day).IsSuccess);
        Assert.True(lot.SetWithdrawalRestriction(Day.AddDays(3)).IsSuccess);
        _lots.Rows.Add(lot);

        var movement = EggInventoryMovement.Create(
            MovementId, AccountId, LotId, EggMovementType.Void, 25, "SalesOrder", OrderId, "returned");
        // PostgreSQL stamps CreatedAtUtc (#819); set it the way a read hands it back.
        typeof(EggInventoryMovement)
            .GetProperty(nameof(EggInventoryMovement.CreatedAtUtc), BindingFlags.Public | BindingFlags.Instance)!
            .SetValue(movement, MovedAt);
        _movements.Rows.Add(movement);
    }

    private static readonly DailyEntryDetails ExpectedAdjusted = new(
        AdjustedEntryId, FarmId, HouseId, FlockId, Day, DailyEntryStatus.ManagerAdjusted,
        102, 5, 3, 2, 7, CrackedId, DirtyId,
        [new GradeQuantityDto(LargeId, 62), new GradeQuantityDto(MediumId, 30)],
        4, "recount", null, LockedAt,
        """{"totalEggs":100,"crackedEggs":4,"dirtyEggs":3,"discardedEggs":2,"mortalityCount":1,"grades":[{"eggGradeId":"00000000-0000-0000-0000-0000000000c1","quantity":60},{"eggGradeId":"00000000-0000-0000-0000-0000000000c2","quantity":31}]}""");

    private static readonly DailyEntryDetails ExpectedVoided = new(
        VoidedEntryId, FarmId, HouseId, FlockId, Day.AddDays(-1), DailyEntryStatus.Voided,
        10, 0, 0, 0, 0, null, null, [new GradeQuantityDto(LargeId, 10)],
        3, null, "wrong flock", null, null);

    private static readonly EggGradeDetails ExpectedCracked = new(
        CrackedId, FarmId, "Cracked", EggGradeType.Quality, 7, true, DailyEntryKind.Cracked, false, 40);

    [Fact]
    public async Task GetDailyEntry_CopiesEveryField_UnknownIdIsNull()
    {
        AssertEntry(ExpectedAdjusted, await Module().GetDailyEntryAsync(AdjustedEntryId, default));
        Assert.Null(await Module().GetDailyEntryAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task ListDailyEntries_ForwardsTheFiltersAndCopiesEveryField()
    {
        var list = await Module().ListDailyEntriesAsync(
            FlockId, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), 25, 50, default);

        Assert.Equal(2, list.Count);
        AssertEntry(ExpectedAdjusted, list[0]);
        AssertEntry(ExpectedVoided, list[1]);
        Assert.Equal([$"list {FlockId} 2026-09-01..2026-09-30 25/50"], _entries.Calls);
    }

    [Fact]
    public async Task Grades_CopyEveryField_AndForwardTheFarm()
    {
        Assert.Equal([ExpectedCracked], await Module().ListActiveGradesAsync(FarmId, default));
        Assert.Equal([ExpectedCracked], await Module().ListAllGradesAsync(default));
        Assert.Equal(ExpectedCracked, await Module().GetGradeAsync(CrackedId, default));
        Assert.Null(await Module().GetGradeAsync(Guid.NewGuid(), default));
        Assert.Equal([$"active {FarmId}", "all"], _grades.Calls);
    }

    [Fact]
    public async Task ListLots_ForwardsTheFiltersAndCopiesEveryField()
    {
        Assert.Equal(
            [new EggLotDetails(LotId, LargeId, Day, 60, 35, Day.AddDays(3), AdjustedEntryId)],
            await Module().ListLotsAsync(LargeId, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), 25, 50, default));
        Assert.Equal([$"lots {LargeId} 2026-09-01..2026-09-30 25/50"], _lots.Calls);
    }

    [Fact]
    public async Task ListLotMovements_CopiesEveryField_UnknownLotIsNullWithoutAMovementRead()
    {
        Assert.Equal(
            [new EggLotMovementDetails(MovementId, EggMovementType.Void, 25, "SalesOrder", OrderId, "returned", MovedAt)],
            await Module().ListLotMovementsAsync(LotId, default));
        Assert.Equal([$"movements {LotId}"], _movements.Calls);

        _movements.Calls.Clear();
        Assert.Null(await Module().ListLotMovementsAsync(Guid.NewGuid(), default));
        Assert.Empty(_movements.Calls);
    }

    [Fact]
    public async Task GetStockByGrade_ForwardsTheDate()
    {
        Assert.Empty(await Module().GetStockByGradeAsync(Day, default));
        Assert.Equal(["stock 2026-09-20"], _lots.Calls);
    }

    [Fact]
    public async Task GradeLookup_ForwardsTheIds()
    {
        var names = await new EggGradeLookup(_grades).GetDisplayNamesAsync([CrackedId, LargeId], default);

        Assert.Equal(new Dictionary<Guid, string> { [CrackedId] = "Cracked" }, names);
        Assert.Equal([$"names {CrackedId},{LargeId}"], _grades.Calls);
    }

    [Fact]
    public async Task DailyEntryLookup_ForwardsTheNaturalKeyAndReturnsTheId()
    {
        var lookup = new DailyEntryLookup(_entries);

        Assert.Equal(AdjustedEntryId, await lookup.FindIdForFlockScopedWriteAsync(
            AccountId, FarmId, HouseId, FlockId, Day, default));
        Assert.Null(await lookup.FindIdForFlockScopedWriteAsync(
            AccountId, FarmId, HouseId, FlockId, Day.AddDays(1), default));
        Assert.Equal(
            [$"write-key {AccountId} {FarmId} {HouseId} {FlockId} 2026-09-20",
             $"write-key {AccountId} {FarmId} {HouseId} {FlockId} 2026-09-21"],
            _entries.Calls);
    }

    // Records compare their Grades list by reference, so compare it apart.
    private static void AssertEntry(DailyEntryDetails expected, DailyEntryDetails? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Grades, actual.Grades);
        Assert.Equal(expected, actual with { Grades = expected.Grades });
    }

    private sealed class FakeEntries : IDailyEntryRepository
    {
        public List<DailyEntry> Rows { get; } = [];
        public List<string> Calls { get; } = [];

        public Task<DailyEntry?> GetReadOnlyAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Rows.SingleOrDefault(e => e.Id == id));

        public Task<IReadOnlyList<DailyEntry>> ListAsync(
            Guid? flockId, DateOnly? from, DateOnly? to, int limit, int offset, CancellationToken ct = default)
        {
            Calls.Add($"list {flockId} {from:yyyy-MM-dd}..{to:yyyy-MM-dd} {limit}/{offset}");
            return Task.FromResult<IReadOnlyList<DailyEntry>>(Rows);
        }

        public Task<DailyEntry?> FindByNaturalKeyForFlockScopedWriteAsync(
            Guid accountId, Guid farmId, Guid houseId, Guid flockId, DateOnly date, CancellationToken ct = default)
        {
            Calls.Add($"write-key {accountId} {farmId} {houseId} {flockId} {date:yyyy-MM-dd}");
            return Task.FromResult(Rows.FirstOrDefault(e =>
                e.FarmId == farmId && e.HouseId == houseId && e.FlockId == flockId && e.Date == date));
        }

        public Task<DailyEntry?> GetByIdForFlockScopedWriteAsync(Guid id, Guid accountId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<DailyEntry>> ListSubmittedBeforeAsync(DateOnly before, int limit, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<DailyEntry?> GetByIdAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AddAsync(DailyEntry entity, CancellationToken ct = default) => throw new NotSupportedException();
        public void Update(DailyEntry entity) => throw new NotSupportedException();
        public void Remove(DailyEntry entity) => throw new NotSupportedException();
    }

    private sealed class FakeGrades : IEggGradeRepository
    {
        public List<EggGrade> Rows { get; } = [];
        public List<string> Calls { get; } = [];

        public Task<IReadOnlyList<EggGrade>> ListActiveAsync(Guid? farmId = null, CancellationToken ct = default)
        {
            Calls.Add($"active {farmId}");
            return Task.FromResult<IReadOnlyList<EggGrade>>(Rows);
        }

        public Task<IReadOnlyList<EggGrade>> ListAllAsync(CancellationToken ct = default)
        {
            Calls.Add("all");
            return Task.FromResult<IReadOnlyList<EggGrade>>(Rows);
        }

        public Task<EggGrade?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Rows.SingleOrDefault(g => g.Id == id));

        public Task<IReadOnlyDictionary<Guid, string>> GetDisplayNamesAsync(
            IReadOnlyCollection<Guid> gradeIds, CancellationToken ct = default)
        {
            Calls.Add($"names {string.Join(",", gradeIds)}");
            return Task.FromResult<IReadOnlyDictionary<Guid, string>>(
                Rows.Where(g => gradeIds.Contains(g.Id)).ToDictionary(g => g.Id, g => g.Name));
        }

        public Task<bool> NameExistsAsync(Guid farmId, string name, Guid? excludeId = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task AddAsync(EggGrade entity, CancellationToken ct = default) => throw new NotSupportedException();
        public void Update(EggGrade entity) => throw new NotSupportedException();
        public void Remove(EggGrade entity) => throw new NotSupportedException();
    }

    private sealed class FakeLots : IEggLotRepository
    {
        public List<EggLot> Rows { get; } = [];
        public List<string> Calls { get; } = [];

        public Task<EggLot?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Rows.SingleOrDefault(l => l.Id == id));

        public Task<IReadOnlyList<EggLot>> ListAsync(
            Guid? eggGradeId, DateOnly? from, DateOnly? to, int limit, int offset, CancellationToken ct = default)
        {
            Calls.Add($"lots {eggGradeId} {from:yyyy-MM-dd}..{to:yyyy-MM-dd} {limit}/{offset}");
            return Task.FromResult<IReadOnlyList<EggLot>>(Rows);
        }

        public Task<IReadOnlyList<StockByGrade>> GetStockByGradeAsync(DateOnly asOfDate, CancellationToken ct = default)
        {
            Calls.Add($"stock {asOfDate:yyyy-MM-dd}");
            return Task.FromResult<IReadOnlyList<StockByGrade>>([]);
        }

        public Task<IReadOnlyList<EggLot>> GetAvailableFifoLockedAsync(
            Guid accountId, IReadOnlyList<Guid> eggGradeIds, DateOnly allocationDate, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<EggLot>> GetByIdsLockedAsync(
            Guid accountId, IReadOnlyList<Guid> lotIds, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<EggLot>> GetByDailyEntryLockedAsync(
            Guid accountId, Guid dailyEntryId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task AddAsync(EggLot entity, CancellationToken ct = default) => throw new NotSupportedException();
        public void Update(EggLot entity) => throw new NotSupportedException();
        public void Remove(EggLot entity) => throw new NotSupportedException();
    }

    private sealed class FakeMovements : IEggInventoryMovementRepository
    {
        public List<EggInventoryMovement> Rows { get; } = [];
        public List<string> Calls { get; } = [];

        public Task<IReadOnlyList<EggInventoryMovement>> ListByLotAsync(Guid eggLotId, CancellationToken ct = default)
        {
            Calls.Add($"movements {eggLotId}");
            return Task.FromResult<IReadOnlyList<EggInventoryMovement>>(Rows.Where(m => m.EggLotId == eggLotId).ToList());
        }

        public Task AddAsync(EggInventoryMovement movement, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AddRangeAsync(IEnumerable<EggInventoryMovement> movements, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
