namespace Cluckwork.Application.Features.Reports;

[ModuleContract("Insights")]
public sealed record ExpenseCategoryTotal(Guid ExpenseCategoryId, string Name, long TotalMinorUnits);
