namespace Cluckwork.Api.IntegrationTests;

using System.Text.RegularExpressions;
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

    // The alias EF gave the outer SalesOrders row. The correlation below is
    // asserted against THAT alias, so a subquery comparing "SalesOrderId" to
    // anything else does not pass for a correlated one.
    private static string OuterOrderAlias(string sql)
    {
        var match = Regex.Match(sql, "FROM \"SalesOrders\" AS (\\w+)");
        Assert.True(match.Success, sql);
        return match.Groups[1].Value;
    }

    // Every correlated Payments subquery in `span`, each as the text from its
    // own FROM "Payments" to the paren that closes the SELECT it sits in: the
    // first unmatched ')' from there, which steps over the NOT (...) inside it.
    // All of them, not the first: UnpaidOnly emits two (the projection and the
    // predicate), and a claim about "the subquery" that reads one of two is the
    // same hole this replaced.
    private static IReadOnlyList<string> PaymentsSubqueries(string span)
    {
        const string marker = "FROM \"Payments\"";
        var found = new List<string>();
        for (var start = span.IndexOf(marker, StringComparison.Ordinal); start >= 0;
             start = span.IndexOf(marker, start + 1, StringComparison.Ordinal))
        {
            var depth = 0;
            var end = -1;
            for (var i = start; i < span.Length && end < 0; i++)
            {
                if (span[i] == '(') depth++;
                else if (span[i] == ')' && depth == 0) end = i;
                else if (span[i] == ')') depth--;
            }
            Assert.True(end >= 0, span);
            found.Add(span[start..end]);
        }
        return found;
    }

    // What the subquery must carry, checked INSIDE its own span. The assertion
    // this replaced was Assert.Contains("ef_filter", sql) over the whole query,
    // which the tenant filters on SalesOrders and SalesOrderItems satisfy on
    // their own: it stayed green with the Payments filter deleted outright, so
    // it proved nothing about the correlated read it named.
    private static void AssertPaymentsSubqueriesAreScopedAndCorrelated(string span, string outerAlias)
    {
        var subqueries = PaymentsSubqueries(span);
        Assert.NotEmpty(subqueries);
        foreach (var subquery in subqueries)
        {
            Assert.Contains("\"AccountId\" = @ef_filter", subquery, StringComparison.Ordinal);
            Assert.Contains($"\"SalesOrderId\" = {outerAlias}.\"Id\"", subquery, StringComparison.Ordinal);
        }
    }

    // The WHERE clause of the page's own subquery: from the WHERE that follows
    // FROM "SalesOrders" to the ORDER BY that closes it. Scoping the search
    // this way is what stops a `> 0` living anywhere else in the query from
    // standing in for the unpaid predicate.
    private static string InnerWhereClause(string sql)
    {
        var from = sql.IndexOf("FROM \"SalesOrders\"", StringComparison.Ordinal);
        Assert.True(from >= 0, sql);
        var where = sql.IndexOf("WHERE", from, StringComparison.Ordinal);
        var order = sql.IndexOf("ORDER BY", from, StringComparison.Ordinal);
        Assert.True(where >= 0 && order > where, sql);
        return sql[where..order];
    }

    // The CASE the SELECT list projects as the settlement figure.
    private static string ProjectedSettlementExpression(string sql)
    {
        var end = sql.IndexOf("END AS c", StringComparison.Ordinal);
        Assert.True(end >= 0, $"No projected settlement CASE in:\n{sql}");
        var start = sql.LastIndexOf("CASE", end, StringComparison.Ordinal);
        Assert.True(start >= 0, sql);
        return sql[start..end];
    }

    // The CASE the WHERE compares to zero.
    private static string FilteredSettlementExpression(string sql)
    {
        var where = InnerWhereClause(sql);
        var end = where.IndexOf("END > 0", StringComparison.Ordinal);
        Assert.True(end >= 0, $"The unpaid filter compares something other than a CASE to zero:\n{where}");
        var start = where.LastIndexOf("CASE", end, StringComparison.Ordinal);
        Assert.True(start >= 0, sql);
        return where[start..end];
    }

    // What the settlement figure must BE, asserted identically wherever it
    // appears. One helper for both spans on purpose: a shape pinned on the
    // projection and not on the predicate is exactly how the two drift apart.
    private static void AssertIsTheSettlementExpression(string expression, string sql)
    {
        var alias = OuterOrderAlias(sql);
        // The whole shape, not a substring of it: the Confirmed guard is what
        // yields NULL off Confirmed, and NULL > 0 is what keeps Draft,
        // Cancelled and Voided out of the filter without a second predicate.
        Assert.Contains(
            $"WHEN {alias}.\"Status\" = 'Confirmed' THEN {alias}.\"TotalMinorUnits\" - (",
            expression, StringComparison.Ordinal);
        // What is subtracted is the SUM of this order's live payments, so the
        // subtrahend is pinned as well as the minuend.
        Assert.Contains("sum(", expression, StringComparison.Ordinal);
        Assert.Contains("\"AmountMinorUnits\"", expression, StringComparison.Ordinal);
        AssertPaymentsSubqueriesAreScopedAndCorrelated(expression, alias);
    }

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
        // The tenant query filter reaches the correlated subquery on its own.
        // It is not written by hand, so nothing in the repository would fail if
        // it stopped being applied, and only the emitted SQL can say that it
        // still is.
        AssertPaymentsSubqueriesAreScopedAndCorrelated(sql, OuterOrderAlias(sql));
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

        // The predicate is the projected figure, compared to zero. Both spans
        // are pulled out and put through the same shape check, because
        // Assert.Contains("> 0") and Assert.Contains("CASE") over the whole
        // query both survived replacing the predicate with
        // `o.TotalAmount.MinorUnits > 0` — which admits a fully paid order
        // into the unpaid filter while the projection still reads correctly.
        AssertIsTheSettlementExpression(ProjectedSettlementExpression(unpaid), unpaid);
        AssertIsTheSettlementExpression(FilteredSettlementExpression(unpaid), unpaid);

        // And the page is otherwise unfiltered: the comparison is the whole of
        // what UnpaidOnly adds, so its absence here is the claim.
        Assert.DoesNotContain("> 0", visible, StringComparison.Ordinal);
        AssertIsTheSettlementExpression(ProjectedSettlementExpression(visible), visible);
    }
}
