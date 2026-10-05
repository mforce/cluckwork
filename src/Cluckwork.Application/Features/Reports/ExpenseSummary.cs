namespace Cluckwork.Application.Features.Reports;

[ModuleContract("Insights")]
public sealed record ExpenseSummary(
    IReadOnlyList<ExpenseCategoryTotal> Categories, long GrandTotalMinorUnits,
    string CurrencyCode, int CurrencyMinorUnit);
