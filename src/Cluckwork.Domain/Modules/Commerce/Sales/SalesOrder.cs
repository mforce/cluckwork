using Cluckwork.Domain.Modules.Commerce.Contracts;

namespace Cluckwork.Domain.Modules.Commerce.Sales;

public sealed class SalesOrder : AggregateRoot<Guid>, IMutableRecord
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
    /// code except <see cref="Contracts.DiscountReasonCode.Other"/>, which is
    /// meaningless without it. Never non-null while the code is null.
    /// </summary>
    public string? DiscountReasonNote { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
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
        Guid productId, Cluckwork.Domain.Modules.Commerce.Contracts.ProductType productTypeSnapshot, Guid eggGradeId,
        Cluckwork.Domain.Modules.Commerce.Contracts.ProductUnit unit, int baseUnitFactor, int quantity, Money unitPrice,
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
    // even attempt a lock or a FIFO query). #721 put the discount-reason rules
    // here for that same reason, rather than only inside Confirm: a missing
    // reason would otherwise plan and apply a whole FIFO allocation before
    // rolling it back.
    //
    // The ORDER of these checks is what decides which error a caller sees when
    // two could fire, so it is pinned test by test in
    // SalesOrderDiscountReasonTests.
    public Result CheckCanConfirm(DiscountReasonCode? discountReasonCode, string? discountReasonNote)
    {
        if (Status != SalesOrderStatus.Draft)
            return Result.Failure(Error.Domain(
                "SalesOrder.NotDraft", "Only draft orders can be confirmed."));
        if (_items.Count == 0)
            return Result.Failure(Error.Domain(
                "SalesOrder.NoItems", "Cannot confirm an order with no items."));

        // #720 R10, same reasoning: a cast can produce a value no member names,
        // and it must not persist. Checked before the rules below, which reason
        // about named members and are meaningless for a value that is not one.
        if (discountReasonCode is { } code && !Enum.IsDefined(code))
            throw new ArgumentOutOfRangeException(
                nameof(discountReasonCode), discountReasonCode,
                "DiscountReasonCode must be a defined member.");

        var note = NormalizeNote(discountReasonNote);
        if (HasBelowListLine && discountReasonCode is null)
            return Result.Failure(Error.Validation(
                "SalesOrder.DiscountReasonRequired",
                "A discount reason is required: at least one line is priced below its list price."));
        if (discountReasonCode == Cluckwork.Domain.Modules.Commerce.Contracts.DiscountReasonCode.Other && note is null)
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

        return Result.Success();
    }

    // Blank is the same as absent — the caller cannot express "a note made only
    // of spaces", and the column must not hold one.
    private static string? NormalizeNote(string? note)
    {
        var trimmed = note?.Trim();
        return trimmed?.Length == 0 ? null : trimmed;
    }

    /// <summary>
    /// True when at least one line was sold under a comparable list price.
    /// A NULL list price is not a discount (the catalogue said nothing to
    /// compare against) and neither is a price ABOVE list, so the comparison is
    /// a strict <c>&lt;</c> — the same two branches the SPA's lineDiscount uses.
    /// </summary>
    public bool HasBelowListLine => _items.Any(
        i => i.ListUnitPriceMinorUnits is { } list && i.UnitPrice.MinorUnits < list);

    /// <summary>
    /// The line this order should be refused for under <paramref name="ceiling"/>,
    /// or null when every line is within it (#727).
    /// </summary>
    /// <remarks>
    /// Pure: no mutation, no <c>Version</c> bump, no role and no ceiling
    /// LOOKUP. The caller decides WHO is bound; this decides only whether the
    /// order is over.
    /// <para>
    /// Deliberately OUTSIDE <see cref="CheckCanConfirm"/> and deliberately not
    /// re-run by <see cref="Confirm"/>: that method holds rules about the
    /// ORDER, it runs before the role is known, and Confirm re-runs it at
    /// mutation time — so folding an ACTOR rule in would make the aggregate
    /// refuse a transition that is legal for an Owner.
    /// </para>
    /// <para>
    /// Reports the WORST offender rather than the first in item order, so the
    /// refusal names the most egregious line. An unmeasurable line outranks
    /// every measurable breach: a discount nobody can compute cannot be
    /// compared against one that is known.
    /// </para>
    /// </remarks>
    public CeilingBreach? FindCeilingBreach(DiscountCeiling ceiling)
    {
        SalesOrderItem? worst = null;
        foreach (var item in _items)
        {
            switch (item.AgainstCeiling(ceiling))
            {
                case LineCeilingStatus.Unmeasurable:
                    return new CeilingBreach(item.EggGradeId, LineCeilingStatus.Unmeasurable, null);
                case LineCeilingStatus.Exceeds:
                    if (worst is null || DiscountsDeeperThan(item, worst)) worst = item;
                    break;
            }
        }

        return worst is null
            ? null
            : new CeilingBreach(worst.EggGradeId, LineCeilingStatus.Exceeds, DiscountPercentOf(worst));
    }

    // Compares the two ratios EXACTLY, never the rounded decimals
    // DiscountPercentOf returns: at a full-width list price two genuinely
    // different discounts round to the same decimal, and the refusal would
    // then name the wrong line. The refusal itself stays correct either way —
    // this decides only which line it names.
    //
    //   (la − ua)/la > (lb − ub)/lb   ⇔   (la − ua)·lb > (lb − ub)·la
    //
    // Cross-multiplied in Int128 for the same reason IsExceededBy is, and
    // safe to multiply without a sign flip because AgainstCeiling only returns
    // Exceeds for a positive list price.
    private static bool DiscountsDeeperThan(SalesOrderItem a, SalesOrderItem b)
    {
        Int128 la = a.ListUnitPriceMinorUnits!.Value;
        Int128 lb = b.ListUnitPriceMinorUnits!.Value;
        return (la - a.UnitPrice.MinorUnits) * lb > (lb - b.UnitPrice.MinorUnits) * la;
    }

    // Exact and unrounded — the handler decides how many decimals to print, and
    // the SPA renders its own with the farm's separator. Taken in decimal
    // rather than long so a full-width list price cannot wrap the subtraction.
    // AgainstCeiling only returns Exceeds for a POSITIVE list price, so there is
    // nothing here to divide by zero.
    private static decimal DiscountPercentOf(SalesOrderItem item)
    {
        var list = item.ListUnitPriceMinorUnits!.Value;
        return ((decimal)list - item.UnitPrice.MinorUnits) * 100m / list;
    }

    // #721 — the reason is required at confirm, not at draft time, and the rule
    // lives on the aggregate rather than in a validator because the seeders
    // call the handler directly and never see one (#394).
    public Result Confirm(DiscountReasonCode? discountReasonCode, string? discountReasonNote)
    {
        var guard = CheckCanConfirm(discountReasonCode, discountReasonNote);
        if (guard.IsFailure) return guard;

        DiscountReasonCode = discountReasonCode;
        DiscountReasonNote = NormalizeNote(discountReasonNote);
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
