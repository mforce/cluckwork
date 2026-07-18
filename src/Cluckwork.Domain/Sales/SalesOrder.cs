namespace Cluckwork.Domain.Sales;

public sealed class SalesOrder : AggregateRoot<Guid>
{
    public const int MaxVoidReasonLength = 500;

    private readonly List<SalesOrderItem> _items = [];

    public string ReferenceNumber { get; private set; } = string.Empty;
    public Guid CustomerId { get; private set; }
    public SalesOrderStatus Status { get; private set; }
    public DateOnly OrderDate { get; private set; }
    public Money TotalAmount { get; private set; } = null!;
    public string? VoidReason { get; private set; }
    public int Version { get; private set; }

    public IReadOnlyList<SalesOrderItem> Items => _items.AsReadOnly();

    private SalesOrder() { }

    public static SalesOrder Create(
        Guid id, Guid accountId, Guid customerId,
        string referenceNumber, DateOnly orderDate, string currencyCode, int currencyMinorUnit = 2)
    {
        return new SalesOrder
        {
            Id = id, AccountId = accountId,
            CustomerId = customerId,
            ReferenceNumber = referenceNumber,
            OrderDate = orderDate,
            Status = SalesOrderStatus.Draft,
            // The order snapshots the farm currency INCLUDING its minor unit —
            // JPY(0)/KWD(3) amounts are misread if this defaults to cents.
            TotalAmount = Money.Zero(currencyCode, currencyMinorUnit)
        };
    }

    public Result<SalesOrderItem> AddItem(Guid eggGradeId, int quantity, Money unitPrice)
    {
        if (Status != SalesOrderStatus.Draft)
            return Result.Failure<SalesOrderItem>(Error.Domain(
                "SalesOrder.NotDraft", "Items can only be added to draft orders."));

        var item = SalesOrderItem.Create(AccountId, Id, eggGradeId, quantity, unitPrice);
        _items.Add(item);
        RecalculateTotal();
        // Version is the concurrency token (EF never auto-increments it): without
        // this bump, two parallel item mutations both match WHERE Version = N and
        // the second silently overwrites the first's TotalAmount.
        Version++;
        return Result.Success(item);
    }

    public Result RemoveItem(Guid itemId)
    {
        if (Status != SalesOrderStatus.Draft)
            return Result.Failure(Error.Domain(
                "SalesOrder.NotDraft", "Items can only be removed from draft orders."));

        var item = _items.FirstOrDefault(i => i.Id == itemId);
        if (item is null)
            return Result.Failure(Error.NotFound(nameof(SalesOrderItem), itemId));

        _items.Remove(item);
        RecalculateTotal();
        Version++;
        return Result.Success();
    }

    public Result UpdateItem(Guid itemId, int quantity, Money unitPrice)
    {
        if (Status != SalesOrderStatus.Draft)
            return Result.Failure(Error.Domain(
                "SalesOrder.NotDraft", "Items can only be edited on draft orders."));

        var item = _items.FirstOrDefault(i => i.Id == itemId);
        if (item is null)
            return Result.Failure(Error.NotFound(nameof(SalesOrderItem), itemId));

        item.Update(quantity, unitPrice);
        RecalculateTotal();
        Version++;
        return Result.Success();
    }

    // Recomputed from the lines rather than adjusted incrementally — one source
    // of truth, no drift across add/remove/update paths.
    private void RecalculateTotal() =>
        TotalAmount = _items.Aggregate(
            Money.Zero(TotalAmount.CurrencyCode, TotalAmount.CurrencyMinorUnit),
            (acc, i) => acc.Add(i.LineTotal));

    public Result Cancel()
    {
        if (Status != SalesOrderStatus.Draft)
            return Result.Failure(Error.Domain(
                "SalesOrder.NotDraft", "Only draft orders can be cancelled."));
        Status = SalesOrderStatus.Cancelled;
        Version++;
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

    // Undo of a mistaken confirm (#60) — the caller must restore the allocated
    // stock to its source lots in the same transaction. Not returns processing:
    // a voided order keeps its lines and total for the audit trail.
    public Result Void(string reason)
    {
        if (Status == SalesOrderStatus.Voided)
            return Result.Failure(Error.Domain(
                "SalesOrder.AlreadyVoided", "This order is already voided."));
        if (Status != SalesOrderStatus.Confirmed)
            return Result.Failure(Error.Domain(
                "SalesOrder.NotConfirmed", "Only confirmed orders can be voided."));
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation(
                "SalesOrder.VoidReasonRequired", "A void reason is required."));
        if (reason.Trim().Length > MaxVoidReasonLength)
            return Result.Failure(Error.Validation(
                "SalesOrder.VoidReasonTooLong",
                $"Void reason must be at most {MaxVoidReasonLength} characters."));

        Status = SalesOrderStatus.Voided;
        VoidReason = reason.Trim();
        Version++;
        return Result.Success();
    }
}

public enum SalesOrderStatus { Draft, Confirmed, Shipped, Invoiced, Cancelled, Voided }

public sealed class SalesOrderItem : Entity<Guid>
{
    public Guid SalesOrderId { get; private set; }
    public Guid EggGradeId { get; private set; }
    public int Quantity { get; private set; }
    public Money UnitPrice { get; private set; } = null!;
    public Money LineTotal => UnitPrice.Multiply(Quantity);

    private SalesOrderItem() { }

    internal void Update(int quantity, Money unitPrice)
    {
        Quantity = quantity;
        UnitPrice = unitPrice;
    }

    // Id left unset for EF to generate: a client-set key on an item added to an
    // already-tracked order is discovered as Modified (UPDATE of a nonexistent
    // row) — same trap as daily-entry grade lines.
    internal static SalesOrderItem Create(
        Guid accountId, Guid orderId,
        Guid eggGradeId, int quantity, Money unitPrice)
    {
        return new SalesOrderItem
        {
            AccountId = accountId,
            SalesOrderId = orderId,
            EggGradeId = eggGradeId,
            Quantity = quantity,
            UnitPrice = unitPrice
        };
    }
}

public sealed record SalesOrderConfirmedEvent(Guid OrderId, Guid AccountId) : IDomainEvent;
