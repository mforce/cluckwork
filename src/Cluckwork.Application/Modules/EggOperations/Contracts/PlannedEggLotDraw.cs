namespace Cluckwork.Application.Modules.EggOperations.Contracts;

[ModuleContract("EggOperations")]
public sealed record PlannedEggLotDraw(Guid LineId, Guid EggLotId, int Quantity);
