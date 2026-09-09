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

    // The line snapshots EVERYTHING it needs to stay meaningful forever (spec
    // §10.5): the product type, the grade the product mapped to at this moment
    // (re-pointing the mapping later must not rewrite history), and the
    // eggs-per-unit factor (§9.7 — redefining a carton must not reinterpret
    // recorded orders).
    public Result<SalesOrderItem> AddItem(
        Guid productId, Catalog.ProductType productTypeSnapshot, Guid eggGradeId,
        Catalog.ProductUnit unit, int baseUnitFactor, int quantity, Money unitPrice,
        long? listUnitPriceMinorUnits = null, ListPriceBasis listPriceBasis = ListPriceBasis.Recorded)
    {
        if (Status != SalesOrderStatus.Draft)
            return Result.Failure<SalesOrderItem>(Error.Domain(
                "SalesOrder.NotDraft", "Items can only be added to draft orders."));
        if (baseUnitFactor < 1)
            return Result.Failure<SalesOrderItem>(Error.Validation(
                "SalesOrder.InvalidUnitFactor", "Eggs per unit must be at least 1."));
        // quantity × factor is int math — wrap-around would allocate garbage.
        if (quantity > 0 && quantity > int.MaxValue / baseUnitFactor)
            return Result.Failure<SalesOrderItem>(Error.Validation(
                "SalesOrder.QuantityTooLarge", "The line exceeds the supported egg count."));

        var item = SalesOrderItem.Create(
            AccountId, Id, productId, productTypeSnapshot, eggGradeId,
            unit, baseUnitFactor, quantity, unitPrice, listUnitPriceMinorUnits, listPriceBasis);
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

        // Same int-overflow guard as AddItem: a wrapped-negative QuantityBase
        // would sail through allocation (no positive remainder) and confirm a
        // sale that consumed no stock (codex review of #100).
        if (quantity > 0 && quantity > int.MaxValue / item.BaseUnitFactor)
            return Result.Failure(Error.Validation(
                "SalesOrder.QuantityTooLarge", "The line exceeds the supported egg count."));

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

    // #612 — the precondition, without mutating, so ConfirmSaleHandler can
    // check it before touching stock (a NotDraft/NoItems refusal must never
    // even attempt a lock or a FIFO query).
    public Result CheckCanConfirm()
    {
        if (Status != SalesOrderStatus.Draft)
            return Result.Failure(Error.Domain(
                "SalesOrder.NotDraft", "Only draft orders can be confirmed."));
        if (_items.Count == 0)
            return Result.Failure(Error.Domain(
                "SalesOrder.NoItems", "Cannot confirm an order with no items."));
        return Result.Success();
    }

    public Result Confirm()
    {
        var guard = CheckCanConfirm();
        if (guard.IsFailure) return guard;

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

// #720 — why a line's ListUnitPriceMinorUnits is what it is. NULL alone cannot
// say, and #727 gates an approval on the difference: for ProductUnpriced and
// NotComparable, "no comparable list price" is a RECORDED FACT and no discount
// is computable; for PreDating it means "we do not know", and the line may have
// been deeply discounted. Those two need opposite treatment.
//
// PreDating is written by the backfill only. Nothing in the application ever
// sets it — a row the code writes always knows its own basis.
public enum ListPriceBasis
{
    /// <summary>A comparable list price was captured; ListUnitPriceMinorUnits is non-null.</summary>
    Recorded,
    /// <summary>The product had no default price at all.</summary>
    ProductUnpriced,
    /// <summary>The product's currency code or minor unit did not match the order's.</summary>
    NotComparable,
    /// <summary>The row predates the column. Backfill only — never written by the application.</summary>
    PreDating,
}

public sealed class SalesOrderItem : Entity<Guid>
{
    public Guid SalesOrderId { get; private set; }
    public Guid ProductId { get; private set; }
    // Snapshots at line creation (spec §10.5/§9.7) — never re-resolved.
    public Catalog.ProductType ProductTypeSnapshot { get; private set; }
    public Guid EggGradeId { get; private set; }
    public Catalog.ProductUnit Unit { get; private set; }
    public int BaseUnitFactor { get; private set; }
    /// <summary>Quantity in selling units (dozens, cartons, ...).</summary>
    public int Quantity { get; private set; }
    /// <summary>Always individual eggs: Quantity × BaseUnitFactor. Allocation runs on this.</summary>
    public int QuantityBase { get; private set; }
    /// <summary>Price per selling unit.</summary>
    public Money UnitPrice { get; private set; } = null!;
    /// <summary>
    /// The product's list price at the moment this line was added, in the
    /// ORDER's currency and minor unit — see AddOrderItemHandler, which is the
    /// only thing that sets it and only when those agree. <see cref="Update"/>
    /// does not re-resolve it: editing a line's quantity or price never
    /// changes what the catalogue said when the line was added (INV-1). NULL
    /// means "no comparable list price", which covers three cases the read
    /// surfaces deliberately render alike: the product had none, the line
    /// predates the column, or the denominations did not match (#720). That
    /// denomination check is what makes a bare long? honest, and it backstops
    /// a state the #123 currency lock makes unreachable today — a priced
    /// product locks the farm currency (CurrencyBoundRowProbe.cs:24).
    /// </summary>
    public long? ListUnitPriceMinorUnits { get; private set; }
    /// <summary>
    /// Why <see cref="ListUnitPriceMinorUnits"/> is what it is (#720). Paired with
    /// it by construction: Recorded IFF the value is non-null. Set once at line
    /// creation and never re-resolved, exactly like the value itself (INV-1).
    /// </summary>
    public ListPriceBasis ListPriceBasis { get; private set; }
    public Money LineTotal => UnitPrice.Multiply(Quantity);

    private SalesOrderItem() { }

    internal void Update(int quantity, Money unitPrice)
    {
        Quantity = quantity;
        QuantityBase = quantity * BaseUnitFactor;
        UnitPrice = unitPrice;
    }

    // Id left unset for EF to generate: a client-set key on an item added to an
    // already-tracked order is discovered as Modified (UPDATE of a nonexistent
    // row) — same trap as daily-entry grade lines.
    internal static SalesOrderItem Create(
        Guid accountId, Guid orderId, Guid productId,
        Catalog.ProductType productTypeSnapshot, Guid eggGradeId,
        Catalog.ProductUnit unit, int baseUnitFactor, int quantity, Money unitPrice,
        long? listUnitPriceMinorUnits = null, ListPriceBasis listPriceBasis = ListPriceBasis.Recorded)
    {
        return new SalesOrderItem
        {
            AccountId = accountId,
            SalesOrderId = orderId,
            ProductId = productId,
            ProductTypeSnapshot = productTypeSnapshot,
            EggGradeId = eggGradeId,
            Unit = unit,
            BaseUnitFactor = baseUnitFactor,
            Quantity = quantity,
            QuantityBase = quantity * baseUnitFactor,
            UnitPrice = unitPrice,
            ListUnitPriceMinorUnits = listUnitPriceMinorUnits,
            ListPriceBasis = listPriceBasis
        };
    }
}

public sealed record SalesOrderConfirmedEvent(Guid OrderId, Guid AccountId) : IDomainEvent;
