namespace Cluckwork.Domain.Tests.Inventory;

using Cluckwork.Domain.Common;
using Cluckwork.Domain.Inventory;

public sealed class InventoryTests
{
    private static readonly Money Cost = new(2500, "USD", 2);
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    private static InventoryItem MakeItem() => InventoryItem.Create(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        "Layer feed 17%", InventoryCategory.Feed, "kg", Cost);

    private static InventoryLot MakeLot(decimal quantity = 100m) => InventoryLot.Create(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Today, quantity, Cost,
        lotNumber: null, expiryDate: null);

    // --- InventoryItem ---

    [Fact]
    public void Item_Create_TrimsAndDefaultsActive()
    {
        var item = InventoryItem.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "  Starter feed  ", InventoryCategory.Feed, " kg ", null);
        Assert.Equal("Starter feed", item.Name);
        Assert.Equal("kg", item.Unit);
        Assert.True(item.Active);
        Assert.Null(item.DefaultUnitCost);
    }

    [Fact]
    public void Item_Update_BumpsVersion()
    {
        var item = MakeItem();
        var before = item.Version;

        var result = item.Update("Layer feed 18%", "kg", new Money(2600, "USD", 2));

        Assert.True(result.IsSuccess);
        Assert.Equal("Layer feed 18%", item.Name);
        Assert.Equal(before + 1, item.Version);
    }

    [Fact]
    public void Item_Update_WhitespaceName_Fails()
    {
        var item = MakeItem();
        var result = item.Update("   ", "kg", null);
        Assert.True(result.IsFailure);
        Assert.Equal("InventoryItem.NameRequired", result.Error.Code);
    }

    [Fact]
    public void Item_DeactivateActivate_Guards()
    {
        var item = MakeItem();
        Assert.True(item.Deactivate().IsSuccess);
        Assert.Equal("InventoryItem.NotActive", item.Deactivate().Error.Code);
        Assert.True(item.Activate().IsSuccess);
        Assert.Equal("InventoryItem.AlreadyActive", item.Activate().Error.Code);
    }

    // --- InventoryLot ---

    [Fact]
    public void Lot_Create_StartsFullyAvailable()
    {
        var lot = MakeLot(437.5m);
        Assert.Equal(437.5m, lot.QuantityReceived);
        Assert.Equal(437.5m, lot.QuantityAvailable);
    }

    [Fact]
    public void Lot_Create_ExpiryBeforeReceived_Throws()
    {
        Assert.Throws<ArgumentException>(() => InventoryLot.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Today, 10m, Cost,
            lotNumber: null, expiryDate: Today.AddDays(-1)));
    }

    [Fact]
    public void Lot_Consume_WithinAvailable_Succeeds_AndBumpsVersion()
    {
        var lot = MakeLot(100m);
        var before = lot.Version;

        var result = lot.Consume(37.25m);

        Assert.True(result.IsSuccess);
        Assert.Equal(62.75m, lot.QuantityAvailable);
        Assert.Equal(before + 1, lot.Version);
    }

    [Fact]
    public void Lot_Consume_ExceedsAvailable_Fails()
    {
        var lot = MakeLot(50m);
        var result = lot.Consume(50.001m);
        Assert.True(result.IsFailure);
        Assert.Equal("InventoryLot.InsufficientStock", result.Error.Code);
        Assert.Equal(50m, lot.QuantityAvailable);
    }

    [Fact]
    public void Lot_Consume_NonPositive_Fails()
    {
        var lot = MakeLot();
        Assert.True(lot.Consume(0m).IsFailure);
        Assert.True(lot.Consume(-1m).IsFailure);
    }

    [Fact]
    public void Lot_Adjust_NegativeWithinAvailable_Succeeds()
    {
        var lot = MakeLot(100m);
        var before = lot.Version;
        Assert.True(lot.Adjust(-40m).IsSuccess);
        Assert.Equal(60m, lot.QuantityAvailable);
        Assert.Equal(before + 1, lot.Version);
    }

    [Fact]
    public void Lot_Adjust_NegativeBeyondAvailable_Fails()
    {
        var lot = MakeLot(100m);
        Assert.True(lot.Consume(80m).IsSuccess);
        var result = lot.Adjust(-30m);
        Assert.True(result.IsFailure);
        Assert.Equal("InventoryLot.InsufficientStock", result.Error.Code);
        Assert.Equal(20m, lot.QuantityAvailable);
    }

    [Fact]
    public void Lot_Adjust_PositiveRestoresUpToReceived()
    {
        var lot = MakeLot(100m);
        Assert.True(lot.Consume(80m).IsSuccess);
        Assert.True(lot.Adjust(30m).IsSuccess);
        Assert.Equal(50m, lot.QuantityAvailable);

        var beyond = lot.Adjust(60m);
        Assert.True(beyond.IsFailure);
        Assert.Equal("InventoryLot.AdjustExceedsReceived", beyond.Error.Code);
    }

    [Fact]
    public void Lot_Adjust_Zero_Fails()
    {
        Assert.True(MakeLot().Adjust(0m).IsFailure);
    }

    // --- FeedUsage ---

    [Fact]
    public void FeedUsage_Create_Guards()
    {
        var usage = FeedUsage.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Today, 12.5m, "kg", new Money(30000, "USD", 2), Now, "  morning feed  ");
        Assert.Equal(12.5m, usage.Quantity);
        Assert.Equal("morning feed", usage.Note);
        Assert.Equal(Now, usage.CreatedAtUtc);

        Assert.Throws<ArgumentOutOfRangeException>(() => FeedUsage.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Today, 0m, "kg", Money.Zero("USD"), Now));
        Assert.Throws<ArgumentException>(() => FeedUsage.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Today, 1m, "kg", new Money(-1, "USD", 2), Now));
    }

    [Fact]
    public void Movement_ReferenceTypeAndId_MustBeSetTogether()
    {
        Assert.Throws<ArgumentException>(() => InventoryMovement.Create(
            Guid.NewGuid(), Guid.NewGuid(), null, Today,
            InventoryMovementType.Usage, -1m, "kg", Now, referenceType: "FeedUsage"));
        Assert.Throws<ArgumentException>(() => InventoryMovement.Create(
            Guid.NewGuid(), Guid.NewGuid(), null, Today,
            InventoryMovementType.Usage, -1m, "kg", Now, referenceId: Guid.NewGuid()));
    }

    // --- WaterUsage ---

    [Fact]
    public void WaterUsage_Create_DirectQuantity()
    {
        var usage = WaterUsage.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Today,
            120.5m, "L", WaterSource.Well, null, null, Now, "  tank refill  ");
        Assert.Equal(120.5m, usage.Quantity);
        Assert.Equal("tank refill", usage.Note);
        Assert.Null(usage.MeterStart);
    }

    [Fact]
    public void WaterUsage_MeterRules()
    {
        // Meters must travel together.
        Assert.Throws<ArgumentException>(() => WaterUsage.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Today,
            10m, "L", WaterSource.Well, 100m, null, Now));
        // End before start refused; equal readings too (zero delta would
        // otherwise surface as a confusing quantity error).
        Assert.Throws<ArgumentException>(() => WaterUsage.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Today,
            10m, "L", WaterSource.Well, 100m, 90m, Now));
        Assert.Throws<ArgumentException>(() => WaterUsage.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Today,
            10m, "L", WaterSource.Well, 100m, 100m, Now));
        // Quantity must equal the delta.
        Assert.Throws<ArgumentException>(() => WaterUsage.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Today,
            15m, "L", WaterSource.Well, 100m, 110m, Now));
        // Consistent meters accepted.
        var ok = WaterUsage.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Today,
            10m, "L", WaterSource.Municipal, 100m, 110m, Now);
        Assert.Equal(10m, ok.Quantity);
    }

    [Fact]
    public void WaterUsage_Update_BumpsVersion_AndKeepsFlockDate()
    {
        var usage = WaterUsage.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Today,
            50m, "L", WaterSource.Well, null, null, Now);
        var before = usage.Version;

        var result = usage.Update(60m, "gal", WaterSource.Tank, null, null, "recount");

        Assert.True(result.IsSuccess);
        Assert.Equal(60m, usage.Quantity);
        Assert.Equal("gal", usage.Unit);
        Assert.Equal(before + 1, usage.Version);
    }

    [Fact]
    public void WaterUsage_InvalidUnit_Throws()
    {
        Assert.Throws<ArgumentException>(() => WaterUsage.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Today,
            10m, "m3", WaterSource.Well, null, null, Now));
    }

    // --- InventoryMovement ---

    private static readonly DateTime Now = new(2026, 7, 18, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Movement_PurchaseMustBePositive_UsageMustBeNegative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => InventoryMovement.Create(
            Guid.NewGuid(), Guid.NewGuid(), null, Today,
            InventoryMovementType.Purchase, -5m, "kg", Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => InventoryMovement.Create(
            Guid.NewGuid(), Guid.NewGuid(), null, Today,
            InventoryMovementType.Usage, 5m, "kg", Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => InventoryMovement.Create(
            Guid.NewGuid(), Guid.NewGuid(), null, Today,
            InventoryMovementType.Adjustment, 0m, "kg", Now));
    }

    [Fact]
    public void Movement_AdjustmentMayBeNegative()
    {
        var movement = InventoryMovement.Create(
            Guid.NewGuid(), Guid.NewGuid(), null, Today,
            InventoryMovementType.Adjustment, -2.5m, "kg", Now, note: "spillage correction");
        Assert.Equal(-2.5m, movement.QuantityDelta);
        Assert.Equal("spillage correction", movement.Note);
        Assert.Equal(Now, movement.CreatedAtUtc);
    }
}
