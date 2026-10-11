using Cluckwork.Domain.Modules.Commerce.Contracts;

namespace Cluckwork.Domain.Modules.Commerce.Sales;

public sealed class SalesOrderItem : Entity<Guid>, IMutableRecord
{
    public Guid SalesOrderId { get; private set; }
    public Guid ProductId { get; private set; }
    // Snapshots at line creation (spec §10.5/§9.7) — never re-resolved.
    public Cluckwork.Domain.Modules.Commerce.Contracts.ProductType ProductTypeSnapshot { get; private set; }
    public Guid EggGradeId { get; private set; }
    public Cluckwork.Domain.Modules.Commerce.Contracts.ProductUnit Unit { get; private set; }
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
    /// match (#720). #773 reversed the decision to keep the ListPriceBasis
    /// property below off the JSON read API: a bare NULL cannot tell
    /// ProductUnpriced from PreDating, so the screen was telling a reader the
    /// product had no price when the line merely predates the column. The
    /// basis now ships on SalesOrderItemResponse by name, alongside the
    /// Admin-only CSV export that already carried it. That
    /// denomination check is what makes a bare long? honest, and it
    /// backstops a state the #123 currency lock makes unreachable today — a
    /// priced product locks the farm currency (ProductRepository's
    /// ICurrencyBoundRowSource read).
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
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    private SalesOrderItem() { }

    // #727 — the basis routing lives with the entity that owns the basis.
    internal LineCeilingStatus AgainstCeiling(DiscountCeiling ceiling) => ListPriceBasis switch
    {
        // The `> 0` is not redundant with the Recorded/non-null pairing: a list
        // price of ZERO is a legal product price, and a discount is
        // (list − unit) as a fraction OF list, so zero has no fraction to take.
        // It is also what keeps FindCeilingBreach's reported percent total.
        ListPriceBasis.Recorded when ListUnitPriceMinorUnits is { } list && list > 0 =>
            ceiling.IsExceededBy(list, UnitPrice.MinorUnits)
                ? LineCeilingStatus.Exceeds
                : LineCeilingStatus.Within,
        // A ZERO list price sold at a NEGATIVE unit price is a discount from
        // nothing. IsExceededBy says it breaches, but the percent the refusal
        // must print is a division by zero, so it cannot be measured — and
        // everything this switch cannot measure fails CLOSED. Routing it to
        // Within instead, as this arm first did, made it the one fail-OPEN
        // case here and waived the ceiling entirely for that line.
        //
        // Unreachable through the API: both order-item validators refuse a
        // negative unit price. Reachable by a caller that never meets a
        // validator (#394), which is the class this repo treats as real.
        ListPriceBasis.Recorded when ListUnitPriceMinorUnits is 0 && UnitPrice.MinorUnits < 0 =>
            LineCeilingStatus.Unmeasurable,
        // Recorded facts (#720): nothing was discounted from anything. A zero
        // list price sold at or above zero is genuinely no discount.
        ListPriceBasis.Recorded
            or ListPriceBasis.ProductUnpriced
            or ListPriceBasis.NotComparable => LineCeilingStatus.Within,
        // "We do not know", and the line may have been deeply discounted — the
        // enum's own comment. It carries no list price, so it never trips
        // HasBelowListLine and #721 never asks for a reason either; waving it
        // through here would leave an unlimited discount recorded nowhere,
        // which is the exact hole this slice exists to close.
        ListPriceBasis.PreDating => LineCeilingStatus.Unmeasurable,
        // A value no member names — a cast, or a row materialized from a basis
        // this code does not know. Fails closed, same as PreDating, so a fifth
        // member added later refuses until someone routes it deliberately.
        _ => LineCeilingStatus.Unmeasurable,
    };

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
        Cluckwork.Domain.Modules.Commerce.Contracts.ProductType productTypeSnapshot, Guid eggGradeId,
        Cluckwork.Domain.Modules.Commerce.Contracts.ProductUnit unit, int baseUnitFactor, int quantity, Money unitPrice,
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
