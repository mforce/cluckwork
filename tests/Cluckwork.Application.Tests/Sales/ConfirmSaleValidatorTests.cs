namespace Cluckwork.Application.Tests.Sales;

using Cluckwork.Application.Features.Sales.ConfirmSale;
using Cluckwork.Domain.Sales;

// #721 — the boundary half of the discount rule. It exists so a malformed
// confirm is a 400 before any lock is taken; the rules that decide whether a
// reason is REQUIRED live on the aggregate, because the seeders reach Confirm
// without passing through here (#394). Tested at this layer because the
// integration tests can only observe the resulting status code, not which of
// the two layers refused.
public sealed class ConfirmSaleValidatorTests
{
    private static readonly ConfirmSaleValidator Validator = new();

    private static string[] ErrorCodes(ConfirmSaleCommand command) =>
        Validator.Validate(command).Errors.Select(e => e.ErrorCode).ToArray();

    [Fact]
    public void AConfirmWithNoDiscountFieldsAtAll_Passes()
    {
        Assert.Empty(ErrorCodes(new ConfirmSaleCommand(Guid.NewGuid())));
    }

    [Theory]
    [InlineData("Volume")]
    [InlineData("DamagedStock")]
    [InlineData("LongStandingCustomer")]
    [InlineData("ManagerApproved")]
    [InlineData("Other")]
    public void EveryMemberName_Passes(string code)
    {
        Assert.Empty(ErrorCodes(new ConfirmSaleCommand(Guid.NewGuid(), code, "a note")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("volume")]
    [InlineData("Discount")]
    [InlineData("0")]
    [InlineData("99")]
    [InlineData("Volume,Other")]
    public void AnythingElse_IsRefusedAsAnUnknownCode(string code)
    {
        Assert.Equal(
            ["SalesOrder.DiscountReasonCode.Known"],
            ErrorCodes(new ConfirmSaleCommand(Guid.NewGuid(), code)));
    }

    [Fact]
    public void ANoteAtExactlyTheCap_Passes()
    {
        var note = new string('x', SalesOrder.MaxDiscountReasonNoteLength);
        Assert.Empty(ErrorCodes(new ConfirmSaleCommand(Guid.NewGuid(), "Other", note)));
    }

    // Measured on the trimmed value, matching the domain, which stores the
    // trimmed note: refusing a fitting note for its surrounding whitespace
    // would be a surprise.
    [Fact]
    public void ANoteThatOnlyFitsAfterTrimming_Passes()
    {
        var note = new string('x', SalesOrder.MaxDiscountReasonNoteLength);
        Assert.Empty(ErrorCodes(new ConfirmSaleCommand(Guid.NewGuid(), "Other", $"  {note}  ")));
    }

    [Fact]
    public void ANoteOverTheCap_IsRefused()
    {
        var note = new string('x', SalesOrder.MaxDiscountReasonNoteLength + 1);
        Assert.Equal(
            ["SalesOrder.DiscountReasonNote.MaxLength"],
            ErrorCodes(new ConfirmSaleCommand(Guid.NewGuid(), "Other", note)));
    }

    // The validator sees only the command, never the order's lines, so it must
    // NOT try to decide whether a reason was required — that is the aggregate's
    // job and the whole reason the rule is not mirrored here.
    [Fact]
    public void ANoteWithNoCode_PassesTheBoundary_AndIsLeftToTheAggregate()
    {
        Assert.Empty(ErrorCodes(new ConfirmSaleCommand(Guid.NewGuid(), null, "a note")));
    }

    [Fact]
    public void AnEmptyOrderId_IsRefused()
    {
        Assert.Equal(
            ["SalesOrder.SalesOrderId.Required"],
            ErrorCodes(new ConfirmSaleCommand(Guid.Empty)));
    }
}
