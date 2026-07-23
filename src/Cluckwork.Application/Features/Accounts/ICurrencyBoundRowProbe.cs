namespace Cluckwork.Application.Features.Accounts;

// Spec §4.6 "Farm currency change rule" — the currency may only change while
// nothing has bound itself to the current one.
//
// The spec names three tables (sales_orders, payments, expenses) and adds that
// "optional future financial tables should follow the same rule". Two more
// already qualify, and both were found by review of #159 rather than by
// reading the list:
//
//   the product catalog — a product snapshots the farm currency alongside a
//   default price, and an order line taking that default stamps the raw
//   minor-unit integer with the ORDER's currency, so a $12.34 default (1234)
//   survives a change to JPY and sells as ¥1,234;
//
//   feed money — inventory lots and feed usages each store a cost, an item's
//   default cost is what a purchase falls back to when none is given, and
//   RecordFeedUsage sums costs across lots without comparing their currencies.
//
// The rule is not "the three tables §4.6 lists" but "anything that has already
// written down an amount in the current currency".
//
// A port of its own rather than a method on IAccountRepository: the question
// spans half a dozen aggregates, none of which the account owns.
public interface ICurrencyBoundRowProbe
{
    Task<bool> AnyAsync(CancellationToken ct = default);
}
