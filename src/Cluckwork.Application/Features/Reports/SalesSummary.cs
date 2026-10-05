namespace Cluckwork.Application.Features.Reports;

// About the period's ORDERS (order date in range): revenue is their confirmed
// totals; paid is settled payments attached to THOSE orders whenever they were
// received — so outstanding = revenue − paid is the period's open AR.
[ModuleContract("Insights")]
public sealed record SalesSummary(
    int ConfirmedCount, long RevenueMinorUnits, long PaidMinorUnits,
    long OutstandingMinorUnits, int VoidedCount,
    string CurrencyCode, int CurrencyMinorUnit);
