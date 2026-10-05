namespace Cluckwork.Application.Modules.Insights.Contracts;

[ModuleContract("Insights")]
public sealed record ExpenseCategoryTotal(Guid ExpenseCategoryId, string Name, long TotalMinorUnits);
