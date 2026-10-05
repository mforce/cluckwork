namespace Cluckwork.Application.Modules.Insights.Contracts;

// "Basic" deliberately: confirmed revenue − recorded expenses, no COGS or
// inventory valuation. Both operands shipped so the figure is auditable.
public sealed record ProfitReport(
    long RevenueMinorUnits, long ExpensesMinorUnits, long ProfitMinorUnits,
    string CurrencyCode, int CurrencyMinorUnit);
