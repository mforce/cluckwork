namespace Cluckwork.Domain.Sales;

public sealed class SalesOrder : AggregateRoot<Guid>
{
    public const int MaxVoidReasonLength = 500;
    public const int MaxDiscountReasonNoteLength = 500;

    private readonly List<SalesOrderItem> _items = [];

    public string ReferenceNumber { get; private set; } = string.Empty;
    public Guid CustomerId { get; private set; }
    public SalesOrderStatus Status { get; private set; }
    public DateOnly OrderDate { get; private set; }
    public Money TotalAmount { get; private set; } = null!;
    public string? VoidReason { get; private set; }
    /// <summary>
    /// Why this order was sold below list (#721). Set at confirm time and only
    /// then, so a draft never carries a reason it may never use. NULL on a
    /// confirmed order means "not recorded" — an order confirmed before this
    /// shipped, never "no discount"; there is no backfill.
    /// </summary>
    public DiscountReasonCode? DiscountReasonCode { get; private set; }
    /// <summary>
    /// Free text beside <see cref="DiscountReasonCode"/>. Optional for every
    /// code except <see cref="Sales.DiscountReasonCode.Other"/>, which is
    /// meaningless without it. Never non-null while the code is null.
    /// </summary>
    public string? DiscountReasonNote { get; private set; }
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
        long? listUnitPriceMinorUnits, ListPriceBasis listPriceBasis)
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

    /// <summary>
    /// True when at least one line was sold under a comparable list price.
    /// A NULL list price is not a discount (the catalogue said nothing to
    /// compare against) and neither is a price ABOVE list, so the comparison is
    /// a strict <c>&lt;</c> — the same two branches the SPA's lineDiscount uses.
    /// </summary>
    public bool HasBelowListLine => _items.Any(
        i => i.ListUnitPriceMinorUnits is { } list && i.UnitPrice.MinorUnits < list);

    // #721 — the reason is required at confirm, not at draft time, and the
    // whole rule lives here rather than in a validator because the seeders call
    // the handler directly and never see one (#394).
    public Result Confirm(DiscountReasonCode? discountReasonCode, string? discountReasonNote)
    {
        var guard = CheckCanConfirm();
        if (guard.IsFailure) return guard;

        // #720 R10, same reasoning: a cast can produce a value no member names,
        // and it must not persist. Checked before the rules below, which reason
        // about named members and are meaningless for a value that is not one.
        if (discountReasonCode is { } code && !Enum.IsDefined(code))
            throw new ArgumentOutOfRangeException(
                nameof(discountReasonCode), discountReasonCode,
                "DiscountReasonCode must be a defined member.");

        var note = discountReasonNote?.Trim();
        if (note?.Length == 0) note = null;

        if (HasBelowListLine && discountReasonCode is null)
            return Result.Failure(Error.Validation(
                "SalesOrder.DiscountReasonRequired",
                "A discount reason is required: at least one line is priced below its list price."));
        if (discountReasonCode == Sales.DiscountReasonCode.Other && note is null)
            return Result.Failure(Error.Validation(
                "SalesOrder.DiscountReasonNoteRequired",
                "A note is required when the discount reason is Other."));
        // A deliberate refusal, not a convenience. Storing a reason against an
        // order that gave nothing away would put a row in #725's "discounts by
        // reason" totals that is not a discount, and dropping it silently is
        // data loss dressed up as tolerance. The cost is a real race: a line
        // edit committing between the dialog and this call can leave the seller
        // holding a reason they were correctly asked for. The order is under
        // FOR UPDATE and the same race already exists here for stock, so the
        // caller recovers the same way — refetch and retry.
        if ((discountReasonCode is not null || note is not null) && !HasBelowListLine)
            return Result.Failure(Error.Validation(
                "SalesOrder.DiscountReasonNotApplicable",
                "No line is priced below its list price, so this order takes no discount reason."));
        if (note is { Length: > MaxDiscountReasonNoteLength })
            return Result.Failure(Error.Validation(
                "SalesOrder.DiscountReasonNoteTooLong",
                $"Discount reason note must be at most {MaxDiscountReasonNoteLength} characters."));

        DiscountReasonCode = discountReasonCode;
        DiscountReasonNote = note;
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

// #721 — why an order was sold below list. Persisted BY NAME, so reordering
// these members cannot silently relabel historical rows.
public enum DiscountReasonCode
{
    /// <summary>A bulk order earned a lower unit price.</summary>
    Volume,
    /// <summary>The goods were sold cheap because of their condition.</summary>
    DamagedStock,
    /// <summary>A standing customer's negotiated price.</summary>
    LongStandingCustomer,
    /// <summary>A manager authorised the price off the catalogue.</summary>
    ManagerApproved,
    /// <summary>Anything else. Meaningless without a note, so a note is required.</summary>
    Other,
}

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
    /// means "no comparable list price", which covers three cases: the product
    /// had none, the line predates the column, or the denominations did not
    /// match (#720). Not on the JSON read API — the ListPriceBasis property
    /// below carries the distinction — and the screen renders all three
    /// alike, but the Admin-only CSV export carries the basis by name. That
    /// denomination check is what makes a bare long? honest, and it
    /// backstops a state the #123 currency lock makes unreachable today — a
    /// priced product locks the farm currency (CurrencyBoundRowProbe.cs:24).
    /// </summary>
    public long? ListUnitPriceMinorUnits { get; private set; }
    /// <summary>
    /// Why <see cref="ListUnitPriceMinorUnits"/> is what it is (#720). Paired with
    /// it by construction: Recorded IFF the value is non-null — enforced by the
    /// throw in <see cref="SalesOrderItem.Create"/>, not merely documented. Set
    /// once at line creation and never re-resolved, exactly like the value
    /// itself (INV-1).
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
        long? listUnitPriceMinorUnits, ListPriceBasis listPriceBasis)
    {
        // #720 R10 — a cast can produce a value no member names: (ListPriceBasis)99
        // passes the pairing check below (false != false) and is not PreDating,
        // so without this it would persist. Checked FIRST: the later guards
        // reason about named members and are meaningless for a value that is
        // not one.
        if (!Enum.IsDefined(listPriceBasis))
            throw new ArgumentOutOfRangeException(
                nameof(listPriceBasis), listPriceBasis,
                "ListPriceBasis must be a defined member.");
        // #720 R5 — the pairing is enforced here, not merely documented. Recorded
        // means "a comparable list price was captured", so it is true exactly when
        // the value is non-null; anything else is a row that satisfies no reader —
        // Recorded claims a fact it does not have, and #727 routes on that claim.
        // An invariant violation, so it throws rather than returning Result:
        // no caller can supply this by accident once the default is gone.
        if (listUnitPriceMinorUnits is not null != (listPriceBasis == ListPriceBasis.Recorded))
            throw new ArgumentException(
                $"ListPriceBasis.{listPriceBasis} cannot pair with a " +
                $"{(listUnitPriceMinorUnits is null ? "null" : "non-null")} list price.",
                nameof(listPriceBasis));
        // PreDating is written by the backfill only. Enforcing that here turns the
        // enum comment into a rule the application cannot break.
        if (listPriceBasis == ListPriceBasis.PreDating)
            throw new ArgumentException(
                "ListPriceBasis.PreDating is backfill-only and cannot be written by the application.",
                nameof(listPriceBasis));

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
