namespace Cluckwork.Application.Modules.Insights.Contracts;

// "Basic" deliberately: confirmed revenue − recorded expenses, no COGS or
// inventory valuation. Both operands shipped so the figure is auditable.
//
// `FeedCostPerDozenMinorUnits` (#1298): rounded to a whole minor unit,
// midpoint to even; null when the range collected no eggs. Every FeedUsage
// row is in the account's current currency — see ReportQueries.
public sealed record ProfitReport(
    long RevenueMinorUnits, long ExpensesMinorUnits, long ProfitMinorUnits,
    long FeedCostMinorUnits, long? FeedCostPerDozenMinorUnits,
    string CurrencyCode, int CurrencyMinorUnit);
