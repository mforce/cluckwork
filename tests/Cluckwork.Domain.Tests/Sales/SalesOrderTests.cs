namespace Cluckwork.Domain.Tests.Sales;

using Cluckwork.Domain.Common;
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
        order.AddItem(Guid.NewGuid(), 10, Money.Zero("USD"));
        order.Confirm();

        var result = order.Cancel();
        Assert.True(result.IsFailure);
        Assert.Equal("SalesOrder.NotDraft", result.Error.Code);
    }

    [Fact]
    public void RemoveItem_RecalculatesTotal_AndBumpsVersion()
    {
        var order = MakeDraft();
        var keep = order.AddItem(Guid.NewGuid(), 10, new Money(100, "USD", 2)).Value;
        var drop = order.AddItem(Guid.NewGuid(), 5, new Money(200, "USD", 2)).Value;
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
        var item = order.AddItem(Guid.NewGuid(), 10, new Money(100, "USD", 2)).Value;

        var result = order.UpdateItem(item.Id, 4, new Money(250, "USD", 2));

        Assert.True(result.IsSuccess);
        Assert.Equal(1000, order.TotalAmount.MinorUnits);
        Assert.Equal(4, order.Items[0].Quantity);
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
        var item = order.AddItem(Guid.NewGuid(), 10, Money.Zero("USD")).Value;
        order.Confirm();

        var result = order.RemoveItem(item.Id);
        Assert.True(result.IsFailure);
        Assert.Equal("SalesOrder.NotDraft", result.Error.Code);
    }

    [Fact]
    public void AddItem_OnCancelled_Fails()
    {
        var order = MakeDraft();
        order.Cancel();

        var result = order.AddItem(Guid.NewGuid(), 10, Money.Zero("USD"));
        Assert.True(result.IsFailure);
        Assert.Equal("SalesOrder.NotDraft", result.Error.Code);
    }

    private static SalesOrder MakeConfirmed()
    {
        var order = MakeDraft();
        order.AddItem(Guid.NewGuid(), 10, Money.Zero("USD"));
        order.Confirm();
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
