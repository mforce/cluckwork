namespace Cluckwork.Application.Features.Sales.ConfirmSale;

using Cluckwork.Domain.Sales;
using FluentValidation;

// #721 — mirrors only the SYNTACTIC half of SalesOrder.Confirm's discount rule,
// the way VoidSaleValidator mirrors Void's. Whether a reason is REQUIRED depends
// on the order's lines, which only the aggregate can see, and the seeders reach
// Confirm without ever passing through here (#394) — so the rules that matter
// stay on the aggregate and this exists to turn a malformed request into a 400
// instead of a 422.
public sealed class ConfirmSaleValidator : AbstractValidator<ConfirmSaleCommand>
{
    public ConfirmSaleValidator()
    {
        RuleFor(x => x.SalesOrderId).NotEmpty().WithErrorCode("SalesOrder.SalesOrderId.Required");
        // DiscountReason.TryParseCode is the same parser ConfirmSaleHandler
        // uses, so a code this accepts is exactly a code that reaches the
        // aggregate.
        RuleFor(x => x.DiscountReasonCode)
            .Must(c => DiscountReason.TryParseCode(c, out _))
            .WithMessage("Unknown discount reason.")
            .WithErrorCode("SalesOrder.DiscountReasonCode.Known");
        // Length on the trimmed value, matching the domain, which stores the
        // trimmed note: refusing a fitting note for its surrounding whitespace
        // would be a surprise.
        RuleFor(x => x.DiscountReasonNote)
            .Must(n => n is null || n.Trim().Length <= SalesOrder.MaxDiscountReasonNoteLength)
            .WithMessage(
                $"Discount reason note must be at most {SalesOrder.MaxDiscountReasonNoteLength} characters.")
            .WithErrorCode("SalesOrder.DiscountReasonNote.MaxLength");
    }
}
