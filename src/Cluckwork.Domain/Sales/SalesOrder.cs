namespace Cluckwork.Domain.Sales;

public sealed class SalesOrder : AggregateRoot<Guid>
{
    private readonly List<SalesOrderItem> _items = [];

    public string ReferenceNumber { get; private set; } = string.Empty;
    public Guid CustomerId { get; private set; }
    public SalesOrderStatus Status { get; private set; }
    public DateOnly OrderDate { get; private set; }
    public Money TotalAmount { get; private set; } = null!;
    public int Version { get; private set; }

    public IReadOnlyList<SalesOrderItem> Items => _items.AsReadOnly();

    private SalesOrder() { }

    public static SalesOrder Create(
        Guid id, Guid accountId, Guid customerId,
        string referenceNumber, DateOnly orderDate, string currencyCode)
    {
        return new SalesOrder
        {
            Id = id, AccountId = accountId,
            CustomerId = customerId,
            ReferenceNumber = referenceNumber,
            OrderDate = orderDate,
            Status = SalesOrderStatus.Draft,
            TotalAmount = Money.Zero(currencyCode)
        };
    }

    public Result AddItem(Guid itemId, string gradeCode, int quantity, Money unitPrice)
    {
        if (Status != SalesOrderStatus.Draft)
            return Result.Failure(Error.Domain(
                "SalesOrder.NotDraft", "Items can only be added to draft orders."));

        var item = SalesOrderItem.Create(itemId, AccountId, Id, gradeCode, quantity, unitPrice);
        _items.Add(item);
        TotalAmount = TotalAmount.Add(item.LineTotal);
        return Result.Success();
    }

    public Result Confirm()
    {
        if (Status != SalesOrderStatus.Draft)
            return Result.Failure(Error.Domain(
                "SalesOrder.NotDraft", "Only draft orders can be confirmed."));
        if (_items.Count == 0)
            return Result.Failure(Error.Domain(
                "SalesOrder.NoItems", "Cannot confirm an order with no items."));

        Status = SalesOrderStatus.Confirmed;
        Version++;
        RaiseDomainEvent(new SalesOrderConfirmedEvent(Id, AccountId));
        return Result.Success();
    }
}

public enum SalesOrderStatus { Draft, Confirmed, Shipped, Invoiced, Cancelled }

public sealed class SalesOrderItem : Entity<Guid>
{
    public Guid SalesOrderId { get; private set; }
    public string GradeCode { get; private set; } = string.Empty;
    public int Quantity { get; private set; }
    public Money UnitPrice { get; private set; } = null!;
    public Money LineTotal => UnitPrice.Multiply(Quantity);

    private SalesOrderItem() { }

    internal static SalesOrderItem Create(
        Guid id, Guid accountId, Guid orderId,
        string gradeCode, int quantity, Money unitPrice)
    {
        return new SalesOrderItem
        {
            Id = id, AccountId = accountId,
            SalesOrderId = orderId,
            GradeCode = gradeCode,
            Quantity = quantity,
            UnitPrice = unitPrice
        };
    }
}

public sealed record SalesOrderConfirmedEvent(Guid OrderId, Guid AccountId) : IDomainEvent;
