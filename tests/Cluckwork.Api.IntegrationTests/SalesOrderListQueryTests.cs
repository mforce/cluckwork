namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Application.Features.Sales;
using Cluckwork.Domain.Sales;
using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

// #769 — the SQL the orders list emits, read directly. Model-only, no database
// (AccountIdConcurrencyTokenModelTests precedent): what is pinned here is the
// SHAPE of the query, and the behaviour that shape buys is proved separately by
// ReadEndpointTests and RoleMatrixTests against a real Postgres.
//
// Three claims, each of which the design turns on and none of which a
// behavioural test can see:
//   SettlementScope.Hidden must never name Payments — the null outstanding a
//   worker receives is a fact about the query, not a field blanked after it.
//   The settlement branch must still join the items — Include survives the
//   wrapper projection, which is what lets the discount column keep working.
//   Paging must apply before that items join, or a page counts order LINES.
public sealed class SalesOrderListQueryTests
{
    private static SalesOrderRepository BuildRepository()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only;Username=none;Password=none")
            .Options;
        return new SalesOrderRepository(
            new AppDbContext(options, new TenantContext(), new FlockScope()));
    }

    private static string Sql(SettlementScope scope) =>
        BuildRepository().ListQuerySql(
            new SalesOrderListFilter(null, null, null, null, scope), 50, 0);

    [Fact]
    public void Hidden_EmitsSqlThatNeverNamesPayments()
    {
        var sql = Sql(SettlementScope.Hidden);

        // Both spellings: the table is "Payments", and an EF alias for it would
        // still carry the name. A branch that computed the figure and discarded
        // it would pass a null-valued assertion and fail this one.
        Assert.DoesNotContain("Payments", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AmountMinorUnits", sql, StringComparison.Ordinal);

        // The control for the control: the query must still BE a sales-order
        // page, or "contains no Payments" is satisfied by empty SQL.
        Assert.Contains("\"SalesOrders\"", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SettlementScope.Visible)]
    [InlineData(SettlementScope.UnpaidOnly)]
    public void SettlementBranch_CorrelatesPayments_AndStillJoinsTheItems(SettlementScope scope)
    {
        var sql = Sql(scope);

        Assert.Contains("\"Payments\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"Voided\"", sql, StringComparison.Ordinal);
        // The tenant query filter reaches the correlated subquery on its own —
        // it is not written by hand, so nothing in the repository would fail if
        // it stopped being applied.
        Assert.Contains("ef_filter", sql, StringComparison.Ordinal);
        // Include(o => o.Items) survives the wrapper projection. Without this
        // the orders list loses every line item and the discount column (#724)
        // silently reads "Unknown" on every row.
        Assert.Contains("\"SalesOrderItems\"", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void SettlementBranch_PagesOrdersNotOrderLines()
    {
        var sql = Sql(SettlementScope.Visible);

        var limit = sql.IndexOf("LIMIT", StringComparison.Ordinal);
        var itemsJoin = sql.IndexOf("\"SalesOrderItems\"", StringComparison.Ordinal);
        Assert.True(limit >= 0 && itemsJoin >= 0, sql);
        // LIMIT inside an inner subquery, the items join outside it: a page of
        // 50 is 50 ORDERS. Were the join applied first, an order with three
        // lines would eat three of the fifty.
        Assert.True(limit < itemsJoin, sql);
    }

    [Fact]
    public void UnpaidOnly_FiltersOnTheSameExpressionItReturns()
    {
        var visible = Sql(SettlementScope.Visible);
        var unpaid = Sql(SettlementScope.UnpaidOnly);

        // The predicate is the projected figure, compared to zero. A CASE that
        // yields NULL off Confirmed makes `> 0` untrue there, so Draft,
        // Cancelled and Voided drop out without a second status predicate.
        Assert.Contains("> 0", unpaid, StringComparison.Ordinal);
        Assert.DoesNotContain("> 0", visible, StringComparison.Ordinal);
        Assert.Contains("CASE", unpaid, StringComparison.Ordinal);
    }
}
