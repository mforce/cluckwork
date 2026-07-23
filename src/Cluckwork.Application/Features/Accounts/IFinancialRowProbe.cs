namespace Cluckwork.Application.Features.Accounts;

// Spec §4.6 "Farm currency change rule" — the currency may only change while
// the farm has no financial history at all. The three tables named by the spec
// (sales_orders, payments, expenses) each snapshot their own currency at
// creation, so once any row exists, changing the farm currency would leave the
// books reading in two denominations.
//
// A port of its own rather than a method on IAccountRepository: the question
// spans three aggregates none of which the account owns.
public interface IFinancialRowProbe
{
    Task<bool> AnyFinancialRowsAsync(CancellationToken ct = default);
}
