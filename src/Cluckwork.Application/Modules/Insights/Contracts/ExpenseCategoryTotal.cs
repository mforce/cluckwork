namespace Cluckwork.Application.Modules.Insights.Contracts;

public sealed record ExpenseCategoryTotal(Guid ExpenseCategoryId, string Name, long TotalMinorUnits);
