namespace Cluckwork.Domain.Modules.Commerce.Contracts;

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
