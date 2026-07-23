namespace Cluckwork.Infrastructure.Repositories;

using Cluckwork.Application.Features.Accounts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

// Everything that has snapshotted the farm currency, scoped to the current
// tenant by the query filters. Short-circuits: on a working farm the first
// probe usually answers it.
//
// Priced products count (see ICurrencyBoundRowProbe). An unpriced product
// carries a currency column too, but nothing reads it as an amount, so it
// cannot be misread and does not lock the farm out of a currency it has not
// started trading in.
public sealed class CurrencyBoundRowProbe(AppDbContext db) : ICurrencyBoundRowProbe
{
    public async Task<bool> AnyAsync(CancellationToken ct = default) =>
        // The three §4.6 names.
        await db.SalesOrders.AnyAsync(ct)
        || await db.Payments.AnyAsync(ct)
        || await db.Expenses.AnyAsync(ct)
        // A priced product: an order line taking the default re-labels the raw
        // integer with the ORDER's currency.
        || await db.Products.AnyAsync(p => p.DefaultPriceMinorUnits != null, ct)
        // Feed money. Every lot stores a unit cost and every usage an estimated
        // cost; an item's default cost is what a purchase falls back to when no
        // cost is given, which would stamp a new lot in the old currency after
        // a change. Costs from two denominations then get summed as though they
        // were one.
        || await db.InventoryLots.AnyAsync(ct)
        || await db.FeedUsages.AnyAsync(ct)
        || await db.InventoryItems.AnyAsync(i => i.DefaultUnitCost != null, ct);

    // Two of these seven cannot be tested in isolation, and it is worth saying
    // why rather than leaving them looking like coverage gaps (adversarial
    // review of #159 counted them as such).
    //
    // A payment only exists against a sales order, and a feed usage only exists
    // against a lot it drew from — so the SalesOrders and InventoryLots probes
    // above always answer first, and no fixture can reach a farm that has one
    // without the other. They stay because "implied by another row today" is a
    // property of the current write paths, not of the rule: the rule is that a
    // row carrying an amount locks the currency, and these carry one.
}
