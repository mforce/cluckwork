using Cluckwork.Domain.Eggs;

namespace Cluckwork.Application.Features.EggLots;

// One line of a sale: the caller's line id, its grade and the eggs it needs.
[ModuleContract("EggOperations")]
public sealed record SaleDemandLine(Guid LineId, Guid EggGradeId, int Quantity);
