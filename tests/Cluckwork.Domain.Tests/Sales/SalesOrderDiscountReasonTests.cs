namespace Cluckwork.Domain.Tests.Sales;

using Cluckwork.Domain.Catalog;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Sales;

// #721 — the discount-reason rule lives on the aggregate, not in a validator,
// because the seeders reach Confirm through the handler and never see one
// (#394). These tests cover the ORDER of the checks as well as each one: the
// order is what decides which error a caller sees when two rules could fire.
public sealed class SalesOrderDiscountReasonTests
{
    private const long ListPrice = 45;

    private static SalesOrder MakeDraft() => SalesOrder.Create(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        "SO-TEST", DateOnly.FromDateTime(DateTime.Today), "USD");

    // listUnitPrice null means the catalogue said nothing to compare against.
    private static SalesOrder DraftWithLine(long unitPrice, long? listUnitPrice = ListPrice)
    {
        var order = MakeDraft();
        order.AddItem(
            Guid.NewGuid(), ProductType.Egg, Guid.NewGuid(), ProductUnit.Egg, 1, 10,
            new Money(unitPrice, "USD", 2), listUnitPrice,
            listUnitPrice is null ? ListPriceBasis.ProductUnpriced : ListPriceBasis.Recorded);
        return order;
    }

    [Theory]
    [InlineData(44, ListPrice, true)]     // below list
    [InlineData(45, ListPrice, false)]    // exactly at list — a strict < , so not a discount
    [InlineData(46, ListPrice, false)]    // above list is a markup, not a discount
    [InlineData(1, null, false)]          // no comparable list price is not a discount
    public void HasBelowListLine_ComparesStrictlyAgainstTheSnapshottedListPrice(
        long unitPrice, long? listUnitPrice, bool expected)
    {
        Assert.Equal(expected, DraftWithLine(unitPrice, listUnitPrice).HasBelowListLine);
    }

    [Fact]
    public void HasBelowListLine_IsTrue_WhenOnlyOneOfSeveralLinesIsBelowList()
    {
        var order = DraftWithLine(ListPrice);
        order.AddItem(
            Guid.NewGuid(), ProductType.Egg, Guid.NewGuid(), ProductUnit.Egg, 1, 10,
            new Money(1, "USD", 2), ListPrice, ListPriceBasis.Recorded);

        Assert.True(order.HasBelowListLine);
    }

    // Step 1 — CheckCanConfirm stays first. A cancelled order carrying a
    // below-list line and no reason is a lifecycle conflict, not a missing
    // reason, and the caller must be told the one that is actionable.
    [Fact]
    public void Confirm_NotDraft_FailsOnLifecycle_BeforeTheDiscountRules()
    {
        var order = DraftWithLine(1);
        order.Cancel();

        var result = order.Confirm(null, null);

        Assert.True(result.IsFailure);
        Assert.Equal("SalesOrder.NotDraft", result.Error.Code);
    }

    [Fact]
    public void Confirm_NoItems_FailsOnLifecycle_EvenWithAReasonSupplied()
    {
        var result = MakeDraft().Confirm(DiscountReasonCode.Volume, null);

        Assert.True(result.IsFailure);
        Assert.Equal("SalesOrder.NoItems", result.Error.Code);
    }

    // Step 2 — a cast can produce a value no member names. It must not persist,
    // and it is checked before the rules below, which reason about named
    // members and are meaningless for a value that is not one. An AT-LIST order
    // is used deliberately: without the throw this would return NotApplicable.
    [Fact]
    public void Confirm_UndefinedCode_Throws_RatherThanFallingIntoALaterRule()
    {
        var order = DraftWithLine(ListPrice);

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => order.Confirm((DiscountReasonCode)99, null));

        Assert.Equal("discountReasonCode", ex.ParamName);
        Assert.Equal(SalesOrderStatus.Draft, order.Status);
    }

    [Fact]
    public void Confirm_UndefinedCode_OnANonDraftOrder_StillFailsOnLifecycleFirst()
    {
        var order = DraftWithLine(1);
        order.Cancel();

        var result = order.Confirm((DiscountReasonCode)99, null);

        Assert.True(result.IsFailure);
        Assert.Equal("SalesOrder.NotDraft", result.Error.Code);
    }

    // Step 3 — the rule this slice exists for.
    [Fact]
    public void Confirm_BelowList_WithNoReason_IsRefused_AndDoesNotMutate()
    {
        var order = DraftWithLine(1);
        var before = order.Version;

        var result = order.Confirm(null, null);

        Assert.True(result.IsFailure);
        Assert.Equal("SalesOrder.DiscountReasonRequired", result.Error.Code);
        Assert.Equal(SalesOrderStatus.Draft, order.Status);
        Assert.Equal(before, order.Version);
        Assert.Empty(order.DomainEvents);
    }

    [Fact]
    public void Confirm_BelowList_WithANoteButNoCode_IsStillRefusedAsMissingAReason()
    {
        var result = DraftWithLine(1).Confirm(null, "spoiled batch");

        Assert.True(result.IsFailure);
        Assert.Equal("SalesOrder.DiscountReasonRequired", result.Error.Code);
    }

    // Step 4 — Other names nothing on its own.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Confirm_Other_WithoutANote_IsRefused(string? note)
    {
        var result = DraftWithLine(1).Confirm(DiscountReasonCode.Other, note);

        Assert.True(result.IsFailure);
        Assert.Equal("SalesOrder.DiscountReasonNoteRequired", result.Error.Code);
    }

    [Theory]
    [InlineData(DiscountReasonCode.Volume)]
    [InlineData(DiscountReasonCode.DamagedStock)]
    [InlineData(DiscountReasonCode.LongStandingCustomer)]
    [InlineData(DiscountReasonCode.ManagerApproved)]
    public void Confirm_EveryCodeExceptOther_NeedsNoNote(DiscountReasonCode code)
    {
        var order = DraftWithLine(1);

        var result = order.Confirm(code, null);

        Assert.True(result.IsSuccess);
        Assert.Equal(code, order.DiscountReasonCode);
        Assert.Null(order.DiscountReasonNote);
    }

    // Step 4 runs before step 5: Other with no note on an at-list order reports
    // the missing note, not the inapplicable reason. Pinned because both are
    // defensible and the caller's error message depends on which one wins.
    [Fact]
    public void Confirm_Other_WithoutANote_OnAnAtListOrder_ReportsTheMissingNote()
    {
        var result = DraftWithLine(ListPrice).Confirm(DiscountReasonCode.Other, null);

        Assert.True(result.IsFailure);
        Assert.Equal("SalesOrder.DiscountReasonNoteRequired", result.Error.Code);
    }

    // Step 5 — a deliberate refusal. Storing a reason against an order that gave
    // nothing away would put a non-discount into #725's discount totals.
    [Theory]
    [InlineData(ListPrice)]         // at list
    [InlineData(ListPrice + 1)]     // above list
    public void Confirm_WithAReason_OnAnOrderThatIsNotDiscounted_IsRefused(long unitPrice)
    {
        var order = DraftWithLine(unitPrice);

        var result = order.Confirm(DiscountReasonCode.Volume, null);

        Assert.True(result.IsFailure);
        Assert.Equal("SalesOrder.DiscountReasonNotApplicable", result.Error.Code);
        Assert.Equal(SalesOrderStatus.Draft, order.Status);
    }

    [Fact]
    public void Confirm_WithANoteAndNoCode_OnAnOrderThatIsNotDiscounted_IsRefused()
    {
        // Otherwise the pair goes out of step: a stored note with no code beside
        // it, on an order that took no discount at all.
        var result = DraftWithLine(ListPrice).Confirm(null, "just because");

        Assert.True(result.IsFailure);
        Assert.Equal("SalesOrder.DiscountReasonNotApplicable", result.Error.Code);
    }

    [Fact]
    public void Confirm_NoListPriceOnTheLine_TakesNoReason()
    {
        var order = DraftWithLine(1, listUnitPrice: null);

        Assert.True(order.Confirm(null, null).IsSuccess);
        Assert.Null(order.DiscountReasonCode);
    }

    // Step 6 — the note is trimmed and capped, exactly like VoidReason.
    [Fact]
    public void Confirm_TrimsTheNote()
    {
        var order = DraftWithLine(1);

        Assert.True(order.Confirm(DiscountReasonCode.Other, "  cracked shells  ").IsSuccess);
        Assert.Equal("cracked shells", order.DiscountReasonNote);
    }

    [Fact]
    public void Confirm_StoresABlankNoteAsNull()
    {
        var order = DraftWithLine(1);

        Assert.True(order.Confirm(DiscountReasonCode.Volume, "   ").IsSuccess);
        Assert.Null(order.DiscountReasonNote);
    }

    [Fact]
    public void Confirm_NoteAtExactlyTheCap_IsAccepted()
    {
        var order = DraftWithLine(1);
        var note = new string('x', SalesOrder.MaxDiscountReasonNoteLength);

        Assert.True(order.Confirm(DiscountReasonCode.Other, note).IsSuccess);
        Assert.Equal(note, order.DiscountReasonNote);
    }

    [Fact]
    public void Confirm_NoteOverTheCap_IsRefused_MeasuredAfterTrimming()
    {
        var order = DraftWithLine(1);
        var note = new string('x', SalesOrder.MaxDiscountReasonNoteLength + 1);

        var result = order.Confirm(DiscountReasonCode.Other, $"  {note}  ");

        Assert.True(result.IsFailure);
        Assert.Equal("SalesOrder.DiscountReasonNoteTooLong", result.Error.Code);
        Assert.Equal(SalesOrderStatus.Draft, order.Status);
    }

    // Step 7 — the success path, including the Version bump every mutation owes.
    [Fact]
    public void Confirm_BelowList_WithAReason_Stores_Confirms_BumpsVersion_AndRaisesTheEvent()
    {
        var order = DraftWithLine(1);
        var before = order.Version;

        var result = order.Confirm(DiscountReasonCode.DamagedStock, " hail damage ");

        Assert.True(result.IsSuccess);
        Assert.Equal(DiscountReasonCode.DamagedStock, order.DiscountReasonCode);
        Assert.Equal("hail damage", order.DiscountReasonNote);
        Assert.Equal(SalesOrderStatus.Confirmed, order.Status);
        Assert.Equal(before + 1, order.Version);
        Assert.Single(order.DomainEvents);
        Assert.IsType<SalesOrderConfirmedEvent>(order.DomainEvents[0]);
    }

    // The wire parser, shared by ConfirmSaleValidator and ConfirmSaleHandler so
    // the boundary and the fail-closed handler branch cannot disagree.
    [Theory]
    [InlineData("Volume", DiscountReasonCode.Volume)]
    [InlineData("Other", DiscountReasonCode.Other)]
    [InlineData("LongStandingCustomer", DiscountReasonCode.LongStandingCustomer)]
    public void TryParseCode_AcceptsAnExactMemberName(string raw, DiscountReasonCode expected)
    {
        Assert.True(DiscountReason.TryParseCode(raw, out var code));
        Assert.Equal(expected, code);
    }

    [Fact]
    public void TryParseCode_TreatsNullAsAValidAbsence()
    {
        Assert.True(DiscountReason.TryParseCode(null, out var code));
        Assert.Null(code);
    }

    [Theory]
    [InlineData("")]                // Enum.TryParse rejects this outright
    [InlineData("volume")]          // right member, wrong case
    [InlineData(" Volume")]         // Enum.TryParse would trim this
    [InlineData("Discount")]        // not a member
    [InlineData("0")]               // Enum.TryParse accepts a numeric string
    [InlineData("99")]              // ... including one no member names
    [InlineData("Volume,Other")]    // a non-flags enum still parses a combination
    public void TryParseCode_RejectsAnythingElse(string raw)
    {
        Assert.False(DiscountReason.TryParseCode(raw, out var code));
        Assert.Null(code);
    }

    [Fact]
    public void Confirm_AtList_WithNoReason_ConfirmsAndLeavesBothFieldsNull()
    {
        var order = DraftWithLine(ListPrice);

        Assert.True(order.Confirm(null, null).IsSuccess);
        Assert.Equal(SalesOrderStatus.Confirmed, order.Status);
        Assert.Null(order.DiscountReasonCode);
        Assert.Null(order.DiscountReasonNote);
    }
}
