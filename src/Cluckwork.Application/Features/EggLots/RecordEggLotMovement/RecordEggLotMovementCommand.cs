namespace Cluckwork.Application.Features.EggLots.RecordEggLotMovement;

// #406 — standalone stock correction against one egg lot. MovementType:
// "Discard" / "InternalUse" (negative only) or "Reconciliation" (signed —
// a recount may find eggs). QuantityDelta is the signed change to the lot's
// available quantity, matching the ledger row it becomes. Reason is
// mandatory — corrections without a why are audit holes.
public sealed record RecordEggLotMovementCommand(
    Guid EggLotId,
    string MovementType,
    int QuantityDelta,
    string Reason);
