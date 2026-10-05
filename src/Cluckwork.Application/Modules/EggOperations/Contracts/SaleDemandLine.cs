namespace Cluckwork.Application.Modules.EggOperations.Contracts;

// One line of a sale: the caller's line id, its grade and the eggs it needs.
public sealed record SaleDemandLine(Guid LineId, Guid EggGradeId, int Quantity);
