using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Eggs;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Eggs;
using Microsoft.Extensions.Logging;

namespace Cluckwork.Application.Features.EggLots.RecordEggLotMovement;

// The movement as written plus the lot's post-movement balance, so the SPA
// can show the resulting stock without a second read.
[ModuleContract("EggOperations")]
public sealed record RecordEggLotMovementResult(
    Guid MovementId,
    Guid EggLotId,
    string MovementType,
    int QuantityDelta,
    string? Reason,
    DateTimeOffset CreatedAtUtc,
    int QuantityAvailable,
    int Version);
