using Cluckwork.Application.Features.Inventory;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Inventory;

namespace Cluckwork.Application.Tests.Inventory;

// #855: the read paths copy each aggregate field by field into positional
// records, where two Guids, two decimals or two dates can swap without a
// compile error.
public sealed class InventoryModuleTests
{
    private static readonly Guid AccountId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid FarmId = Guid.Parse("00000000-0000-0000-0000-0000000000f1");
    private static readonly Guid ItemId = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    private static readonly Guid UnstockedItemId = Guid.Parse("00000000-0000-0000-0000-0000000000c2");
    private static readonly Guid LotId = Guid.Parse("00000000-0000-0000-0000-0000000000d1");
    private static readonly Guid FlockId = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid UsageId = Guid.Parse("00000000-0000-0000-0000-0000000000e1");
    private static readonly Guid WaterId = Guid.Parse("00000000-0000-0000-0000-0000000000e2");
    private static readonly Guid EntryId = Guid.Parse("00000000-0000-0000-0000-0000000000e3");

    private readonly FakeItems _items = new();
    private readonly FakeLots _lots = new();
    private readonly FakeMovements _movements = new();
    private readonly FakeFeedUsages _feedUsages = new();
    private readonly FakeWaterUsages _waterUsages = new();

    // The handlers sit on the write paths only; these tests exercise the reads.
    private InventoryModule Module() => new(
        _items, _lots, _movements, _feedUsages, _waterUsages,
        null!, null!, null!, null!, null!, null!, null!, null!);

    private static readonly InventoryItemDetails ExpectedItem = new(
        ItemId, FarmId, "Layer feed", InventoryCategory.Feed, "kg",
        new Money(1_250, "KWD", 3), 25m, false, 1);

    private static readonly InventoryItemDetails ExpectedUnstockedItem = new(
        UnstockedItemId, FarmId, "Grit", InventoryCategory.Supplement, "bag", null, 0m, true, 0);

    public InventoryModuleTests()
    {
        var item = InventoryItem.Create(
            ItemId, AccountId, FarmId, "Layer feed", InventoryCategory.Feed, "kg", new Money(1_250, "KWD", 3));
        Assert.True(item.Deactivate().IsSuccess);
        _items.Rows.Add(item);
        _items.Rows.Add(InventoryItem.Create(
            UnstockedItemId, AccountId, FarmId, "Grit", InventoryCategory.Supplement, "bag", null));
        _lots.Stock[ItemId] = 25m;

        var lot = InventoryLot.Create(
            LotId, AccountId, ItemId, new DateOnly(2026, 9, 3), 40m, new Money(1_100, "KWD", 3),
            "LOT-7", new DateOnly(2027, 3, 1));
        Assert.True(lot.Consume(15m).IsSuccess);
        _lots.Rows.Add(lot);

        _movements.Rows.Add(InventoryMovement.Create(
            AccountId, ItemId, LotId, new DateOnly(2026, 9, 20), InventoryMovementType.Usage, -15m, "kg",
            flockId: FlockId, note: "north barn", referenceType: nameof(FeedUsage), referenceId: UsageId));

        _feedUsages.Rows.Add(FeedUsage.Create(
            UsageId, AccountId, FlockId, ItemId, new DateOnly(2026, 9, 20), 15m, "kg",
            new Money(16_500, "KWD", 3), "north barn", EntryId));

        var water = WaterUsage.Create(
            WaterId, AccountId, FlockId, new DateOnly(2026, 9, 21), 250m, "L", WaterSource.Well,
            100m, 350m, "meter read", EntryId);
        Assert.True(water.Update(250m, "L", WaterSource.Tank, 100m, 350m, "meter read").IsSuccess);
        _waterUsages.Rows.Add(water);
    }

    [Fact]
    public async Task ListItems_CopiesEveryFieldAndJoinsTheStockRollUp()
    {
        Assert.Equal([ExpectedItem, ExpectedUnstockedItem], await Module().ListItemsAsync(includeInactive: true, default));
        Assert.Equal(["list True"], _items.Calls);
        Assert.Equal(["stock"], _lots.Calls);
    }

    [Fact]
    public async Task GetItem_CopiesEveryField_UnknownItemReadsNoStock()
    {
        Assert.Equal(ExpectedItem, await Module().GetItemAsync(ItemId, default));
        Assert.Equal(["stock"], _lots.Calls);

        _lots.Calls.Clear();
        Assert.Null(await Module().GetItemAsync(Guid.NewGuid(), default));
        Assert.Empty(_lots.Calls);
    }

    [Fact]
    public async Task ListLots_CopiesEveryField() =>
        Assert.Equal(
            [new InventoryLotDetails(LotId, ItemId, new DateOnly(2026, 9, 3), "LOT-7", new DateOnly(2027, 3, 1),
                40m, 25m, new Money(1_100, "KWD", 3), 1)],
            await Module().ListLotsAsync(ItemId, default));

    [Fact]
    public async Task ListMovements_ForwardsThePageAndCopiesEveryField()
    {
        var movementId = _movements.Rows[0].Id;
        Assert.Equal(
            [new InventoryMovementDetails(movementId, ItemId, LotId, new DateOnly(2026, 9, 20),
                InventoryMovementType.Usage, -15m, "kg", FlockId, "north barn", nameof(FeedUsage), UsageId)],
            await Module().ListMovementsAsync(ItemId, 25, 50, default));
        Assert.Equal([$"movements {ItemId} 25/50"], _movements.Calls);
    }

    [Fact]
    public async Task ListFeedUsage_ForwardsTheFiltersAndCopiesEveryField()
    {
        Assert.Equal(
            [new FeedUsageDetails(UsageId, FlockId, ItemId, new DateOnly(2026, 9, 20), 15m, "kg",
                new Money(16_500, "KWD", 3), "north barn", EntryId, 0)],
            await Module().ListFeedUsageAsync(
                FlockId, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), 25, 50, default));
        Assert.Equal([$"feed {FlockId} 2026-09-01..2026-09-30 25/50"], _feedUsages.Calls);
    }

    [Fact]
    public async Task ListWaterUsage_ForwardsTheFiltersAndCopiesEveryField()
    {
        Assert.Equal(
            [new WaterUsageDetails(WaterId, FlockId, new DateOnly(2026, 9, 21), 250m, "L", WaterSource.Tank,
                100m, 350m, "meter read", EntryId, 1)],
            await Module().ListWaterUsageAsync(
                FlockId, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), 25, 50, default));
        Assert.Equal([$"water {FlockId} 2026-09-01..2026-09-30 25/50"], _waterUsages.Calls);
    }

    private sealed class FakeItems : IInventoryItemRepository
    {
        public List<InventoryItem> Rows { get; } = [];
        public List<string> Calls { get; } = [];

        public Task<IReadOnlyList<InventoryItem>> ListAsync(bool includeInactive = false, CancellationToken ct = default)
        {
            Calls.Add($"list {includeInactive}");
            return Task.FromResult<IReadOnlyList<InventoryItem>>(Rows);
        }

        public Task<InventoryItem?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Rows.SingleOrDefault(i => i.Id == id));

        public Task<bool> NameExistsAsync(Guid farmId, string name, Guid? excludeId = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> HasLotsAsync(Guid itemId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<InventoryItem?> GetByIdLockedAsync(Guid accountId, Guid id, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task AddAsync(InventoryItem entity, CancellationToken ct = default) => throw new NotSupportedException();
        public void Update(InventoryItem entity) => throw new NotSupportedException();
        public void Remove(InventoryItem entity) => throw new NotSupportedException();
    }

    private sealed class FakeLots : IInventoryLotRepository
    {
        public List<InventoryLot> Rows { get; } = [];
        public Dictionary<Guid, decimal> Stock { get; } = [];
        public List<string> Calls { get; } = [];

        public Task<Dictionary<Guid, decimal>> StockByItemAsync(CancellationToken ct = default)
        {
            Calls.Add("stock");
            return Task.FromResult(Stock);
        }

        public Task<IReadOnlyList<InventoryLot>> ListByItemAsync(Guid inventoryItemId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<InventoryLot>>(Rows.Where(l => l.InventoryItemId == inventoryItemId).ToList());

        public Task<IReadOnlyList<InventoryLot>> GetAvailableFifoLockedAsync(
            Guid accountId, Guid inventoryItemId, DateOnly asOfDate, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<InventoryLot?> GetByIdLockedAsync(Guid accountId, Guid lotId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<InventoryLot?> GetByIdAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AddAsync(InventoryLot entity, CancellationToken ct = default) => throw new NotSupportedException();
        public void Update(InventoryLot entity) => throw new NotSupportedException();
        public void Remove(InventoryLot entity) => throw new NotSupportedException();
    }

    private sealed class FakeMovements : IInventoryMovementRepository
    {
        public List<InventoryMovement> Rows { get; } = [];
        public List<string> Calls { get; } = [];

        public Task<IReadOnlyList<InventoryMovement>> ListByItemAsync(
            Guid inventoryItemId, int limit, int offset, CancellationToken ct = default)
        {
            Calls.Add($"movements {inventoryItemId} {limit}/{offset}");
            return Task.FromResult<IReadOnlyList<InventoryMovement>>(Rows);
        }

        public Task<InventoryMovement?> GetByIdAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AddAsync(InventoryMovement entity, CancellationToken ct = default) => throw new NotSupportedException();
        public void Update(InventoryMovement entity) => throw new NotSupportedException();
        public void Remove(InventoryMovement entity) => throw new NotSupportedException();
    }

    private sealed class FakeFeedUsages : IFeedUsageRepository
    {
        public List<FeedUsage> Rows { get; } = [];
        public List<string> Calls { get; } = [];

        public Task<IReadOnlyList<FeedUsage>> ListAsync(
            Guid? flockId, DateOnly? from, DateOnly? to, int limit, int offset, CancellationToken ct = default)
        {
            Calls.Add($"feed {flockId} {from:yyyy-MM-dd}..{to:yyyy-MM-dd} {limit}/{offset}");
            return Task.FromResult<IReadOnlyList<FeedUsage>>(Rows);
        }

        public Task<FeedUsage?> GetByIdAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AddAsync(FeedUsage entity, CancellationToken ct = default) => throw new NotSupportedException();
        public void Update(FeedUsage entity) => throw new NotSupportedException();
        public void Remove(FeedUsage entity) => throw new NotSupportedException();
    }

    private sealed class FakeWaterUsages : IWaterUsageRepository
    {
        public List<WaterUsage> Rows { get; } = [];
        public List<string> Calls { get; } = [];

        public Task<IReadOnlyList<WaterUsage>> ListAsync(
            Guid? flockId, DateOnly? from, DateOnly? to, int limit, int offset, CancellationToken ct = default)
        {
            Calls.Add($"water {flockId} {from:yyyy-MM-dd}..{to:yyyy-MM-dd} {limit}/{offset}");
            return Task.FromResult<IReadOnlyList<WaterUsage>>(Rows);
        }

        public Task<WaterUsage?> GetByIdAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AddAsync(WaterUsage entity, CancellationToken ct = default) => throw new NotSupportedException();
        public void Update(WaterUsage entity) => throw new NotSupportedException();
        public void Remove(WaterUsage entity) => throw new NotSupportedException();
    }
}
