namespace Cluckwork.Application.Modules.EggOperations.Contracts;

// The movement as written plus the lot's post-movement balance, so the SPA
// can show the resulting stock without a second read.
public sealed record RecordEggLotMovementResult(
    Guid MovementId,
    Guid EggLotId,
    string MovementType,
    int QuantityDelta,
    string? Reason,
    DateTimeOffset CreatedAtUtc,
    int QuantityAvailable,
    int Version);
