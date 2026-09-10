namespace Cluckwork.Domain.Tests.Sales;

using Cluckwork.Domain.Common;
using Cluckwork.Domain.Catalog;
using Cluckwork.Domain.Sales;

public sealed class SalesOrderTests
{
    private static SalesOrder MakeDraft() => SalesOrder.Create(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        "SO-TEST", DateOnly.FromDateTime(DateTime.Today), "USD");

    [Fact]
    public void Cancel_Draft_Succeeds_AndBumpsVersion()
    {
        var order = MakeDraft();
        var before = order.Version;

        var result = order.Cancel();

        Assert.True(result.IsSuccess);
        Assert.Equal(SalesOrderStatus.Cancelled, order.Status);
        Assert.Equal(before + 1, order.Version);
    }

    [Fact]
    public void Cancel_Confirmed_Fails()
    {
        var order = MakeDraft();
        order.AddItem(Guid.NewGuid(), ProductType.Egg, Guid.NewGuid(), ProductUnit.Egg, 1, 10, Money.Zero("USD"), null, ListPriceBasis.ProductUnpriced);
        order.Confirm(null, null);

        var result = order.Cancel();
        Assert.True(result.IsFailure);
        Assert.Equal("SalesOrder.NotDraft", result.Error.Code);
    }

    [Fact]
    public void RemoveItem_RecalculatesTotal_AndBumpsVersion()
    {
        var order = MakeDraft();
        var keep = order.AddItem(Guid.NewGuid(), ProductType.Egg, Guid.NewGuid(), ProductUnit.Egg, 1, 10, new Money(100, "USD", 2), null, ListPriceBasis.ProductUnpriced).Value;
        var drop = order.AddItem(Guid.NewGuid(), ProductType.Egg, Guid.NewGuid(), ProductUnit.Egg, 1, 5, new Money(200, "USD", 2), null, ListPriceBasis.ProductUnpriced).Value;
        var before = order.Version;

        var result = order.RemoveItem(drop.Id);

        Assert.True(result.IsSuccess);
        Assert.Single(order.Items);
        Assert.Equal(keep.Id, order.Items[0].Id);
        Assert.Equal(1000, order.TotalAmount.MinorUnits);
        Assert.Equal(before + 1, order.Version);
    }

    [Fact]
    public void UpdateItem_RecalculatesTotal()
    {
        var order = MakeDraft();
        var item = order.AddItem(Guid.NewGuid(), ProductType.Egg, Guid.NewGuid(), ProductUnit.Egg, 1, 10, new Money(100, "USD", 2), null, ListPriceBasis.ProductUnpriced).Value;

        var result = order.UpdateItem(item.Id, 4, new Money(250, "USD", 2));

        Assert.True(result.IsSuccess);
        Assert.Equal(1000, order.TotalAmount.MinorUnits);
        Assert.Equal(4, order.Items[0].Quantity);
    }

    [Fact]
    public void AddItem_StoresTheListPriceItWasGiven()
    {
        var order = SalesOrder.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "SO-1",
            new DateOnly(2026, 1, 1), "USD");

        var item = order.AddItem(
            Guid.NewGuid(), ProductType.Egg, Guid.NewGuid(), ProductUnit.Egg, 1, 10,
            new Money(400, "USD", 2), listUnitPriceMinorUnits: 450, listPriceBasis: ListPriceBasis.Recorded).Value;

        Assert.Equal(450, item.ListUnitPriceMinorUnits);
    }

    [Theory]
    [InlineData(450L, ListPriceBasis.Recorded)]
    [InlineData(null, ListPriceBasis.ProductUnpriced)]
    [InlineData(null, ListPriceBasis.NotComparable)]
    public void AddItem_AcceptsEveryHonestPairing(long? listPrice, ListPriceBasis basis)
    {
        var order = SalesOrder.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "SO-1",
            new DateOnly(2026, 1, 1), "USD");

        var item = order.AddItem(
            Guid.NewGuid(), ProductType.Egg, Guid.NewGuid(), ProductUnit.Egg, 1, 10,
            new Money(400, "USD", 2), listPrice, basis).Value;

        Assert.Equal(listPrice, item.ListUnitPriceMinorUnits);
        Assert.Equal(basis, item.ListPriceBasis);
    }

    [Theory]
    [InlineData(null, ListPriceBasis.Recorded)]      // the zero-value trap
    [InlineData(450L, ListPriceBasis.ProductUnpriced)]
    [InlineData(450L, ListPriceBasis.NotComparable)]
    [InlineData(450L, ListPriceBasis.PreDating)]
    [InlineData(null, ListPriceBasis.PreDating)]
    public void AddItem_RefusesAnImpossiblePairing(long? listPrice, ListPriceBasis basis)
    {
        var order = SalesOrder.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "SO-1",
            new DateOnly(2026, 1, 1), "USD");

        Assert.Throws<ArgumentException>(() => order.AddItem(
            Guid.NewGuid(), ProductType.Egg, Guid.NewGuid(), ProductUnit.Egg, 1, 10,
            new Money(400, "USD", 2), listPrice, basis));
    }

    // #720 R10 — a cast can mint a ListPriceBasis no member names. (ListPriceBasis)99
    // passes the pairing check with either price shape (false != false, since it is
    // not Recorded) and is not PreDating, so without a defined-member check it would
    // persist. Enum.IsDefined is checked BEFORE the pairing check for exactly this.
    [Theory]
    [InlineData(null)]
    [InlineData(450L)]
    public void AddItem_RefusesAnUndefinedBasis(long? listPrice)
    {
        var order = SalesOrder.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "SO-1",
            new DateOnly(2026, 1, 1), "USD");

        Assert.Throws<ArgumentOutOfRangeException>(() => order.AddItem(
            Guid.NewGuid(), ProductType.Egg, Guid.NewGuid(), ProductUnit.Egg, 1, 10,
            new Money(400, "USD", 2), listPrice, (ListPriceBasis)99));
    }

    [Fact]
    public void UpdateItem_LeavesListUnitPriceUntouched()
    {
        var order = SalesOrder.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "SO-1",
            new DateOnly(2026, 1, 1), "USD");
        var item = order.AddItem(
            Guid.NewGuid(), ProductType.Egg, Guid.NewGuid(), ProductUnit.Egg, 1, 10,
            new Money(400, "USD", 2), listUnitPriceMinorUnits: 450, listPriceBasis: ListPriceBasis.Recorded).Value;

        order.UpdateItem(item.Id, 20, new Money(300, "USD", 2));

        // The line records what it was SOLD AGAINST. Editing quantity or price
        // does not change what the list price WAS when the line was written.
        Assert.Equal(450, item.ListUnitPriceMinorUnits);
        Assert.Equal(300, item.UnitPrice.MinorUnits);
    }

    [Fact]
    public void RemoveItem_UnknownItem_NotFound()
    {
        var order = MakeDraft();
        var result = order.RemoveItem(Guid.NewGuid());
        Assert.True(result.IsFailure);
        Assert.Equal("SalesOrderItem.NotFound", result.Error.Code);
    }

    [Fact]
    public void RemoveItem_OnConfirmed_Fails()
    {
        var order = MakeDraft();
        var item = order.AddItem(Guid.NewGuid(), ProductType.Egg, Guid.NewGuid(), ProductUnit.Egg, 1, 10, Money.Zero("USD"), null, ListPriceBasis.ProductUnpriced).Value;
        order.Confirm(null, null);

        var result = order.RemoveItem(item.Id);
        Assert.True(result.IsFailure);
        Assert.Equal("SalesOrder.NotDraft", result.Error.Code);
    }

    [Fact]
    public void AddItem_OnCancelled_Fails()
    {
        var order = MakeDraft();
        order.Cancel();

        var result = order.AddItem(Guid.NewGuid(), ProductType.Egg, Guid.NewGuid(), ProductUnit.Egg, 1, 10, Money.Zero("USD"), null, ListPriceBasis.ProductUnpriced);
        Assert.True(result.IsFailure);
        Assert.Equal("SalesOrder.NotDraft", result.Error.Code);
    }

    // #612 — Confirm delegates to CheckCanConfirm, so ConfirmSaleHandler can
    // run the same precondition before touching stock without mutating.
    [Fact]
    public void CheckCanConfirm_Draft_WithItems_Succeeds_AndDoesNotMutate()
    {
        var order = MakeDraft();
        order.AddItem(Guid.NewGuid(), ProductType.Egg, Guid.NewGuid(), ProductUnit.Egg, 1, 10, Money.Zero("USD"), null, ListPriceBasis.ProductUnpriced);
        var before = order.Version;

        var result = order.CheckCanConfirm();

        Assert.True(result.IsSuccess);
        Assert.Equal(SalesOrderStatus.Draft, order.Status);
        Assert.Equal(before, order.Version);
    }

    [Fact]
    public void CheckCanConfirm_NoItems_Fails_SameCodeAsConfirm()
    {
        var order = MakeDraft();
        var result = order.CheckCanConfirm();
        Assert.True(result.IsFailure);
        Assert.Equal("SalesOrder.NoItems", result.Error.Code);
    }

    [Fact]
    public void CheckCanConfirm_NotDraft_Fails_SameCodeAsConfirm()
    {
        var order = MakeConfirmed();
        var result = order.CheckCanConfirm();
        Assert.True(result.IsFailure);
        Assert.Equal("SalesOrder.NotDraft", result.Error.Code);
    }

    private static SalesOrder MakeConfirmed()
    {
        var order = MakeDraft();
        order.AddItem(Guid.NewGuid(), ProductType.Egg, Guid.NewGuid(), ProductUnit.Egg, 1, 10, Money.Zero("USD"), null, ListPriceBasis.ProductUnpriced);
        order.Confirm(null, null);
        return order;
    }

    [Fact]
    public void Void_Confirmed_Succeeds_StoresReason_AndBumpsVersion()
    {
        var order = MakeConfirmed();
        var before = order.Version;

        var result = order.Void("  Confirmed the wrong order  ");

        Assert.True(result.IsSuccess);
        Assert.Equal(SalesOrderStatus.Voided, order.Status);
        Assert.Equal("Confirmed the wrong order", order.VoidReason);
        Assert.Equal(before + 1, order.Version);
        // Lines and total survive for the audit trail.
        Assert.Single(order.Items);
    }

    [Fact]
    public void Void_Draft_Fails()
    {
        var order = MakeDraft();
        var result = order.Void("mistake");
        Assert.True(result.IsFailure);
        Assert.Equal("SalesOrder.NotConfirmed", result.Error.Code);
    }

    [Fact]
    public void Void_Cancelled_Fails()
    {
        var order = MakeDraft();
        order.Cancel();
        var result = order.Void("mistake");
        Assert.True(result.IsFailure);
        Assert.Equal("SalesOrder.NotConfirmed", result.Error.Code);
    }

    [Fact]
    public void Void_Twice_Fails()
    {
        var order = MakeConfirmed();
        Assert.True(order.Void("mistake").IsSuccess);

        var result = order.Void("again");
        Assert.True(result.IsFailure);
        Assert.Equal("SalesOrder.AlreadyVoided", result.Error.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Void_WithoutReason_Fails(string? reason)
    {
        var order = MakeConfirmed();
        var result = order.Void(reason!);
        Assert.True(result.IsFailure);
        Assert.Equal("SalesOrder.VoidReasonRequired", result.Error.Code);
        Assert.Equal(SalesOrderStatus.Confirmed, order.Status);
    }

    [Fact]
    public void Void_ReasonTooLong_Fails()
    {
        var order = MakeConfirmed();
        var result = order.Void(new string('x', SalesOrder.MaxVoidReasonLength + 1));
        Assert.True(result.IsFailure);
        Assert.Equal("SalesOrder.VoidReasonTooLong", result.Error.Code);
    }
}
