using Cluckwork.Domain.Eggs;

namespace Cluckwork.Application.Features.EggLots;

[ModuleContract("EggOperations")]
public sealed record PlannedEggLotDraw(Guid LineId, Guid EggLotId, int Quantity);
