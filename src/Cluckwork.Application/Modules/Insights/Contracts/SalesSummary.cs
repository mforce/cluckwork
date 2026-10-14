namespace Cluckwork.Application.Modules.Insights.Contracts;

// About the period's ORDERS (order date in range): revenue is their confirmed
// totals; paid is settled payments attached to THOSE orders whenever they were
// received — so outstanding = revenue − paid is the period's open AR.
// EggsSold counts the SAME confirmed orders (QuantityBase, i.e. individual
// eggs), so the egg figure and the money figure can never describe different
// order sets.
public sealed record SalesSummary(
    int ConfirmedCount, long RevenueMinorUnits, long PaidMinorUnits,
    long OutstandingMinorUnits, int VoidedCount,
    int EggsSold, List<GradeTotal> EggsSoldByGrade,
    string CurrencyCode, int CurrencyMinorUnit);
