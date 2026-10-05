namespace Cluckwork.Application.Features.Reports;

[ModuleContract("Insights")]
public sealed record GradeTotal(Guid EggGradeId, string Name, int Quantity);
