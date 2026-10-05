namespace Cluckwork.Application.Modules.EggOperations.Contracts;

public sealed record PlannedEggLotDraw(Guid LineId, Guid EggLotId, int Quantity);
