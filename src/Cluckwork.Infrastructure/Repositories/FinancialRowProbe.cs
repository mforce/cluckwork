namespace Cluckwork.Infrastructure.Repositories;

using Cluckwork.Application.Features.Accounts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

// The three tables §4.6 names, scoped to the current tenant by the query
// filters. Short-circuits: the common answer on a working farm is "yes" from
// the first probe.
public sealed class FinancialRowProbe(AppDbContext db) : IFinancialRowProbe
{
    public async Task<bool> AnyFinancialRowsAsync(CancellationToken ct = default) =>
        await db.SalesOrders.AnyAsync(ct)
        || await db.Payments.AnyAsync(ct)
        || await db.Expenses.AnyAsync(ct);
}
