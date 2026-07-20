namespace Cluckwork.Domain.Eggs;

// Egg movement ledger (#101, spec §9.4–9.5). Every change to a lot's
// QuantityAvailable gets an explicit signed row IN THE SAME TRANSACTION as
// the lot mutation — the cached balance must always equal the sum of its
// movements (tech-spec rule: cached balances only if rebuildable from
// ledgers). Append-only like the audit trail: no Version, no update, no
// delete.
public sealed class EggInventoryMovement : AggregateRoot<Guid>
{
    public const int MaxReferenceTypeLength = 50;
    public const int MaxReasonLength = 500;

    public Guid EggLotId { get; private set; }
    public EggMovementType MovementType { get; private set; }
    /// <summary>Signed change to the lot's available quantity, in individual eggs.</summary>
    public int QuantityDelta { get; private set; }
    // What caused the movement (daily entry, sales order, allocation, ...).
    public string ReferenceType { get; private set; } = string.Empty;
    public Guid ReferenceId { get; private set; }
    public string? Reason { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    private EggInventoryMovement() { }

    public static EggInventoryMovement Create(
        Guid id, Guid accountId, Guid eggLotId, EggMovementType movementType,
        int quantityDelta, string referenceType, Guid referenceId,
        DateTimeOffset createdAtUtc, string? reason = null)
    {
        if (quantityDelta == 0)
            throw new ArgumentException("A movement must change the quantity.", nameof(quantityDelta));
        if (string.IsNullOrWhiteSpace(referenceType) || referenceType.Length > MaxReferenceTypeLength)
            throw new ArgumentException("A valid reference type is required.", nameof(referenceType));

        return new EggInventoryMovement
        {
            Id = id, AccountId = accountId,
            EggLotId = eggLotId,
            MovementType = movementType,
            QuantityDelta = quantityDelta,
            ReferenceType = referenceType,
            ReferenceId = referenceId,
            CreatedAtUtc = createdAtUtc,
            Reason = string.IsNullOrWhiteSpace(reason)
                ? null
                : reason.Trim().Length > MaxReasonLength
                    ? reason.Trim()[..MaxReasonLength] : reason.Trim(),
        };
    }
}

// Spec §9.4 movement types. Production/Sale/Adjustment/Void are written today
// (#101); the rest are reserved for later phases (discard tracking, internal
// use, transfers, reconciliation counts).
public enum EggMovementType
{
    Production, Sale, Adjustment, Discard, InternalUse, Transfer, Reconciliation, Void,
}
