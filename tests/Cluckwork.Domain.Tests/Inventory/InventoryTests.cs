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

    // --- InventoryMovement ---

    [Fact]
    public void Movement_PurchaseMustBePositive_UsageMustBeNegative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => InventoryMovement.Create(
            Guid.NewGuid(), Guid.NewGuid(), null, Today,
            InventoryMovementType.Purchase, -5m, "kg"));
        Assert.Throws<ArgumentOutOfRangeException>(() => InventoryMovement.Create(
            Guid.NewGuid(), Guid.NewGuid(), null, Today,
            InventoryMovementType.Usage, 5m, "kg"));
        Assert.Throws<ArgumentOutOfRangeException>(() => InventoryMovement.Create(
            Guid.NewGuid(), Guid.NewGuid(), null, Today,
            InventoryMovementType.Adjustment, 0m, "kg"));
    }

    [Fact]
    public void Movement_AdjustmentMayBeNegative()
    {
        var movement = InventoryMovement.Create(
            Guid.NewGuid(), Guid.NewGuid(), null, Today,
            InventoryMovementType.Adjustment, -2.5m, "kg", note: "spillage correction");
        Assert.Equal(-2.5m, movement.QuantityDelta);
        Assert.Equal("spillage correction", movement.Note);
    }
}
