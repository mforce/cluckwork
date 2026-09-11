namespace Cluckwork.Domain.Tests.Sales;

using System.Reflection;
using Cluckwork.Domain.Catalog;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Sales;

// #727 — the pure aggregate query. Driven entirely through FindCeilingBreach,
// the public surface ConfirmSaleHandler actually calls, so the basis routing is
// asserted as a caller observes it.
public sealed class SalesOrderCeilingTests
{
    private static readonly DiscountCeiling TenPercent = DiscountCeiling.FromBasisPoints(1_000);

    private static SalesOrder MakeDraft() => SalesOrder.Create(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        "SO-CEILING", new DateOnly(2026, 1, 1), "USD");

    private static SalesOrderItem AddLine(
        SalesOrder order, long unitPrice, long? listPrice, ListPriceBasis basis, Guid? eggGradeId = null) =>
        order.AddItem(
            Guid.NewGuid(), ProductType.Egg, eggGradeId ?? Guid.NewGuid(), ProductUnit.Dozen,
            12, 1, new Money(unitPrice, "USD", 2), listPrice, basis).Value;

    // SalesOrderItem.Create THROWS on PreDating — it is backfill-only, and that
    // guard is a rule worth keeping, not an obstacle to weaken. The only
    // real-world producer of such a row is EF materializing what the #720
    // backfill relabelled, so reproduce that here: build the line through the
    // real factory with the same NULL list price a backfilled row carries, then
    // relabel the basis the way the database would hand it back. Nothing else
    // can open this door.
    private static SalesOrderItem AddPreDatingLine(SalesOrder order, long unitPrice, Guid? eggGradeId = null)
    {
        var item = AddLine(order, unitPrice, null, ListPriceBasis.ProductUnpriced, eggGradeId);
        typeof(SalesOrderItem)
            .GetProperty(nameof(SalesOrderItem.ListPriceBasis), BindingFlags.Public | BindingFlags.Instance)!
            .SetValue(item, ListPriceBasis.PreDating);
        return item;
    }

    // --- one case per ListPriceBasis value ---------------------------------

    [Fact]
    public void ARecordedLineWithinTheCeiling_IsNoBreach()
    {
        var order = MakeDraft();
        AddLine(order, unitPrice: 900, listPrice: 1_000, ListPriceBasis.Recorded);

        Assert.Null(order.FindCeilingBreach(TenPercent));
    }

    [Fact]
    public void ARecordedLinePastTheCeiling_Breaches_WithTheExactPercent()
    {
        var order = MakeDraft();
        var grade = Guid.NewGuid();
        AddLine(order, unitPrice: 800, listPrice: 1_000, ListPriceBasis.Recorded, grade);

        var breach = order.FindCeilingBreach(TenPercent);

        Assert.NotNull(breach);
        Assert.Equal(LineCeilingStatus.Exceeds, breach!.Value.Status);
        Assert.Equal(grade, breach.Value.EggGradeId);
        Assert.Equal(20m, breach.Value.DiscountPercent);
    }

    // A recorded fact, not an unknown: the product had no list price at all, so
    // nothing was discounted from anything.
    [Fact]
    public void AProductUnpricedLine_NeverBreaches()
    {
        var order = MakeDraft();
        AddLine(order, unitPrice: 1, listPrice: null, ListPriceBasis.ProductUnpriced);

        Assert.Null(order.FindCeilingBreach(DiscountCeiling.FromBasisPoints(0)));
    }

    // Also a recorded fact: the denominations did not match, so no comparison
    // exists.
    [Fact]
    public void ANotComparableLine_NeverBreaches()
    {
        var order = MakeDraft();
        AddLine(order, unitPrice: 1, listPrice: null, ListPriceBasis.NotComparable);

        Assert.Null(order.FindCeilingBreach(DiscountCeiling.FromBasisPoints(0)));
    }

    // The opposite treatment, and the whole point of the four-value basis: we do
    // not know what this line was discounted from, so it fails closed.
    [Fact]
    public void APreDatingLine_IsUnmeasurable_AndReportsNoPercent()
    {
        var order = MakeDraft();
        var grade = Guid.NewGuid();
        AddPreDatingLine(order, unitPrice: 1, grade);

        var breach = order.FindCeilingBreach(TenPercent);

        Assert.NotNull(breach);
        Assert.Equal(LineCeilingStatus.Unmeasurable, breach!.Value.Status);
        Assert.Equal(grade, breach.Value.EggGradeId);
        Assert.Null(breach.Value.DiscountPercent);
    }

    // A zero list price has no fraction to take, so it is not a discount even
    // at a zero ceiling — and it is what keeps the reported percent total.
    [Fact]
    public void AZeroListPriceLine_NeverBreaches()
    {
        var order = MakeDraft();
        AddLine(order, unitPrice: 0, listPrice: 0, ListPriceBasis.Recorded);

        Assert.Null(order.FindCeilingBreach(DiscountCeiling.FromBasisPoints(0)));
    }

    // The row above passes with or without AgainstCeiling's `> 0` guard, so it
    // does not pin it. THIS one does. A NEGATIVE unit price against a zero list
    // price is the single input for which the cross-multiplication reports
    // Exceeds, and the percent behind it is (0 − unit) × 100 / 0 — so without
    // the guard FindCeilingBreach raises DivideByZeroException and the confirm
    // path answers a representable sale with a 500.
    //
    // No writer can produce it: AddOrderItemValidator floors the unit price at
    // 0, and SalesOrder.AddItem takes any Money because Money is signed. So
    // this pins the guard against a state the domain can REPRESENT, not against
    // a reachable sale.
    [Fact]
    public void ALineSoldBelowAZeroListPrice_DoesNotDivideByZero()
    {
        var order = MakeDraft();
        AddLine(order, unitPrice: -100, listPrice: 0, ListPriceBasis.Recorded);

        Assert.Null(order.FindCeilingBreach(DiscountCeiling.FromBasisPoints(0)));
    }

    // --- worst-offender selection ------------------------------------------

    // The first breaching line in item order is deliberately NOT the one
    // reported: the refusal must name the most egregious line, so the seller
    // knows what to ask a manager for.
    [Fact]
    public void FindCeilingBreach_ReportsTheDeepestDiscount_NotTheFirstInItemOrder()
    {
        var order = MakeDraft();
        var mild = Guid.NewGuid();
        var deepest = Guid.NewGuid();
        var middling = Guid.NewGuid();
        AddLine(order, unitPrice: 880, listPrice: 1_000, ListPriceBasis.Recorded, mild);      // 12%
        AddLine(order, unitPrice: 600, listPrice: 1_000, ListPriceBasis.Recorded, deepest);   // 40%
        AddLine(order, unitPrice: 750, listPrice: 1_000, ListPriceBasis.Recorded, middling);  // 25%

        var breach = order.FindCeilingBreach(TenPercent);

        Assert.NotNull(breach);
        Assert.Equal(deepest, breach!.Value.EggGradeId);
        Assert.Equal(40m, breach.Value.DiscountPercent);
    }

    // An unknown discount cannot be compared against a known one, so it wins
    // however deep the measured breach is — and however late it sorts.
    [Fact]
    public void AnUnmeasurableLine_OutranksADeeperMeasuredBreach()
    {
        var order = MakeDraft();
        var deepest = Guid.NewGuid();
        var unknown = Guid.NewGuid();
        AddLine(order, unitPrice: 1, listPrice: 1_000, ListPriceBasis.Recorded, deepest);     // 99.9%
        AddPreDatingLine(order, unitPrice: 900, unknown);

        var breach = order.FindCeilingBreach(TenPercent);

        Assert.NotNull(breach);
        Assert.Equal(LineCeilingStatus.Unmeasurable, breach!.Value.Status);
        Assert.Equal(unknown, breach.Value.EggGradeId);
        Assert.Null(breach.Value.DiscountPercent);
    }

    [Fact]
    public void FindCeilingBreach_IgnoresLinesWithinTheCeiling()
    {
        var order = MakeDraft();
        var breaching = Guid.NewGuid();
        AddLine(order, unitPrice: 900, listPrice: 1_000, ListPriceBasis.Recorded);            // exactly 10%
        AddLine(order, unitPrice: 1_200, listPrice: 1_000, ListPriceBasis.Recorded);          // above list
        AddLine(order, unitPrice: 850, listPrice: 1_000, ListPriceBasis.Recorded, breaching); // 15%

        var breach = order.FindCeilingBreach(TenPercent);

        Assert.NotNull(breach);
        Assert.Equal(breaching, breach!.Value.EggGradeId);
    }

    // Pure: no mutation, no Version bump, no state change. ConfirmSaleHandler
    // calls it between two locks and before any stock is touched.
    [Fact]
    public void FindCeilingBreach_MutatesNothing()
    {
        var order = MakeDraft();
        AddLine(order, unitPrice: 500, listPrice: 1_000, ListPriceBasis.Recorded);
        var version = order.Version;
        var status = order.Status;

        order.FindCeilingBreach(TenPercent);
        order.FindCeilingBreach(DiscountCeiling.FromBasisPoints(0));

        Assert.Equal(version, order.Version);
        Assert.Equal(status, order.Status);
    }

    // The ceiling is an ACTOR rule, so the aggregate's own transition rules must
    // stay ignorant of it: an Owner confirming a deeply discounted order is
    // legal, and CheckCanConfirm never sees a ceiling to refuse it with.
    [Fact]
    public void CheckCanConfirm_IsUnaffectedByABreachingLine()
    {
        var order = MakeDraft();
        AddLine(order, unitPrice: 1, listPrice: 1_000, ListPriceBasis.Recorded);

        Assert.Equal(
            LineCeilingStatus.Exceeds,
            order.FindCeilingBreach(TenPercent)!.Value.Status);
        Assert.True(order.CheckCanConfirm(DiscountReasonCode.ManagerApproved, null).IsSuccess);
        Assert.True(order.Confirm(DiscountReasonCode.ManagerApproved, null).IsSuccess);
    }
}
