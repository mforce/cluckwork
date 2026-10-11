using Cluckwork.Domain.Modules.Commerce.Contracts;

namespace Cluckwork.Domain.Modules.Commerce.Sales;

public static class DiscountReason
{
    /// <summary>
    /// Parses the wire form of <see cref="DiscountReasonCode"/>: the exact
    /// member name, nothing else. A null input is a valid absence, not a
    /// failure. This is the ONE parser — the confirm endpoint's validator uses
    /// it to turn a malformed request into a 400, and ConfirmSaleHandler uses
    /// it to refuse a caller that never passes a validator (#394) — so the two
    /// cannot disagree about what a legal code is.
    /// </summary>
    /// <remarks>
    /// Three things Enum.TryParse alone would let through, each rejected here:
    /// a numeric string ("0"), a number no member names ("99"), and a
    /// comma-separated combination ("Volume,Other", which a non-flags enum
    /// still parses). The round-trip comparison against the member name is what
    /// catches the first and third.
    /// </remarks>
    public static bool TryParseCode(string? raw, out DiscountReasonCode? code)
    {
        code = null;
        if (raw is null) return true;
        if (!Enum.TryParse(raw, ignoreCase: false, out DiscountReasonCode parsed)
            || !Enum.IsDefined(parsed)
            || parsed.ToString() != raw)
            return false;
        code = parsed;
        return true;
    }
}
