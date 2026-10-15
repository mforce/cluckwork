using Cluckwork.Application.Modules.Insights.Contracts;
using Cluckwork.Application.Modules.Insights.Reports;
using Cluckwork.Domain.Modules.Commerce.Contracts;
using Cluckwork.Domain.Modules.EggOperations.Contracts;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cluckwork.Infrastructure.Modules.Insights.Repositories;

public sealed partial class ReportQueries(AppDbContext db) : IReportQueries
{
    // Official entries only: Draft isn't submitted, Voided vacated its day (#82).
    private static readonly DailyEntryStatus[] OfficialStatuses =
        [DailyEntryStatus.Submitted, DailyEntryStatus.Locked, DailyEntryStatus.ManagerAdjusted];

    public async Task<SalesSummary> GetSalesAsync(
        DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var confirmed = await db.SalesOrders
            .Where(o => o.Status == SalesOrderStatus.Confirmed
                     && o.OrderDate >= from && o.OrderDate <= to)
            .Select(o => new { o.Id, Total = o.TotalAmount.MinorUnits })
            .ToListAsync(ct);

        var orderIds = confirmed.Select(o => o.Id).ToList();
        var paid = orderIds.Count == 0 ? 0L : await db.Payments
            .Where(p => !p.Voided && orderIds.Contains(p.SalesOrderId))
            .SumAsync(p => p.AmountMinorUnits, ct);

        var voidedCount = await db.SalesOrders
            .CountAsync(o => o.Status == SalesOrderStatus.Voided
                          && o.OrderDate >= from && o.OrderDate <= to, ct);

        // Egg lines of exactly the materialized order set (orderIds) above, so
        // a concurrent confirm/void cannot split eggs from revenue.
        var eggLines = new List<GradeTotal>();
        if (orderIds.Count > 0)
        {
            var grouped = await db.SalesOrderItems
                .Where(i => i.ProductTypeSnapshot == ProductType.Egg
                         && orderIds.Contains(i.SalesOrderId))
                .GroupBy(i => i.EggGradeId)
                .Select(g => new { EggGradeId = g.Key, Quantity = g.Sum(i => i.QuantityBase) })
                .ToListAsync(ct);

            // Names resolve the way GetProductionAsync's breakdown does: one lookup
            // for the ids the grouping produced, "?" when a grade has since gone.
            var eggGradeNames = await db.EggGrades
                .Where(g => grouped.Select(e => e.EggGradeId).Contains(g.Id))
                .ToDictionaryAsync(g => g.Id, g => g.Name, ct);

            eggLines = grouped
                .Select(e => new GradeTotal(
                    e.EggGradeId, eggGradeNames.GetValueOrDefault(e.EggGradeId, "?"), e.Quantity))
                .OrderByDescending(t => t.Quantity)
                .ToList();
        }

        var revenue = confirmed.Sum(o => o.Total);
        var (code, minor) = await AccountCurrencyAsync(ct);
        return new SalesSummary(
            confirmed.Count, revenue, paid, revenue - paid, voidedCount,
            eggLines.Sum(e => e.Quantity), eggLines,
            code, minor);
    }

    public async Task<ExpenseSummary> GetExpensesAsync(
        DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var perCategory = await db.Expenses
            .Where<Cluckwork.Domain.Modules.Finance.Expenses.Expense>(e => e.Date >= from && e.Date <= to)
            .GroupBy(e => e.ExpenseCategoryId)
            .Select(g => new { CategoryId = g.Key, Total = g.Sum(e => e.AmountMinorUnits) })
            .ToListAsync(ct);
        var names = await db.ExpenseCategories
            .Where(c => perCategory.Select(p => p.CategoryId).Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        var (code, minor) = await AccountCurrencyAsync(ct);
        return new ExpenseSummary(
            perCategory
                .Select(p => new ExpenseCategoryTotal(
                    p.CategoryId, names.GetValueOrDefault(p.CategoryId, "?"), p.Total))
                .OrderByDescending(p => p.TotalMinorUnits)
                .ToList(),
            perCategory.Sum(p => p.Total),
            code, minor);
    }

    public async Task<ProfitReport> GetProfitAsync(
        DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var revenue = await db.SalesOrders
            .Where(o => o.Status == SalesOrderStatus.Confirmed
                     && o.OrderDate >= from && o.OrderDate <= to)
            .SumAsync(o => o.TotalAmount.MinorUnits, ct);
        var expenses = await db.Expenses
            .Where<Cluckwork.Domain.Modules.Finance.Expenses.Expense>(e => e.Date >= from && e.Date <= to)
            .SumAsync(e => e.AmountMinorUnits, ct);

        var (code, minor) = await AccountCurrencyAsync(ct);

        // #1298 — spec §19.3. The denominator is the same official eggs the
        // production report counts (OfficialStatuses above); rounded to a whole
        // minor unit, midpoint to even; null, not 0, when the range collected
        // no eggs. No per-row currency filter: every FeedUsage row is in the
        // account's current currency, because FeedUsageRepository implements
        // Farm's ICurrencyBoundRowSource, so Account.CurrencyLocked forbids a
        // currency change while feed rows exist (#1097).
        var feedCost = await db.FeedUsages
            .Where(u => u.Date >= from && u.Date <= to)
            .SumAsync(u => u.EstimatedCost.MinorUnits, ct);
        var totalEggs = await db.DailyEntries
            .Where(e => e.Date >= from && e.Date <= to && OfficialStatuses.Contains(e.Status))
            .SumAsync(e => e.TotalEggs, ct);

        return new ProfitReport(
            revenue, expenses, revenue - expenses,
            feedCost,
            totalEggs > 0 ? (long)Math.Round(feedCost * 12m / totalEggs) : null,
            code, minor);
    }

    private async Task<(string Code, int Minor)> AccountCurrencyAsync(CancellationToken ct)
    {
        Cluckwork.Domain.Modules.Farm.Accounts.Account? account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(ct);
        return (account?.DefaultCurrencyCode ?? "", account?.DefaultCurrencyMinorUnit ?? 2);
    }
}
