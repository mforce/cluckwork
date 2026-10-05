namespace Cluckwork.Application.Modules.Insights.Contracts;

[ModuleContract("Insights")]
public sealed record ExpenseSummary(
    IReadOnlyList<ExpenseCategoryTotal> Categories, long GrandTotalMinorUnits,
    string CurrencyCode, int CurrencyMinorUnit);
