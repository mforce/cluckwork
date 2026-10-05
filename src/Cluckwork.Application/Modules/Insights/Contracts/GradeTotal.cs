namespace Cluckwork.Application.Modules.Insights.Contracts;

[ModuleContract("Insights")]
public sealed record GradeTotal(Guid EggGradeId, string Name, int Quantity);
