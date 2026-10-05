namespace Cluckwork.Application.Features.Reports;

// "Basic" deliberately: confirmed revenue − recorded expenses, no COGS or
// inventory valuation. Both operands shipped so the figure is auditable.
[ModuleContract("Insights")]
public sealed record ProfitReport(
    long RevenueMinorUnits, long ExpensesMinorUnits, long ProfitMinorUnits,
    string CurrencyCode, int CurrencyMinorUnit);
