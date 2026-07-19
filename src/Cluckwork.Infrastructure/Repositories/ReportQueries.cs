namespace Cluckwork.Infrastructure.Repositories;

using Cluckwork.Application.Features.Reports;
using Cluckwork.Domain.Eggs;
using Cluckwork.Domain.Sales;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed class ReportQueries(AppDbContext db) : IReportQueries
{
    // Official entries only: Draft isn't submitted, Voided vacated its day (#82).
    private static readonly DailyEntryStatus[] OfficialStatuses =
        [DailyEntryStatus.Submitted, DailyEntryStatus.Locked, DailyEntryStatus.ManagerAdjusted];

    public async Task<ProductionReport> GetProductionAsync(
        DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var perDay = await db.DailyEntries
            .Where(e => e.Date >= from && e.Date <= to && OfficialStatuses.Contains(e.Status))
            .GroupBy(e => e.Date)
            .Select(g => new
            {
                Date = g.Key,
                Total = g.Sum(e => e.TotalEggs),
                Cracked = g.Sum(e => e.CrackedEggs),
                Dirty = g.Sum(e => e.DirtyEggs),
                Discarded = g.Sum(e => e.DiscardedEggs),
                Deaths = g.Sum(e => e.MortalityCount),
            })
            .ToDictionaryAsync(x => x.Date, ct);

        // Period grade totals (per-day × per-grade would bloat the payload).
        var gradeTotals = await db.DailyEntryGrades
            .Where(g => db.DailyEntries.Any(e =>
                e.Id == g.DailyEntryId && e.Date >= from && e.Date <= to
                && OfficialStatuses.Contains(e.Status)))
            .GroupBy(g => g.EggGradeId)
            .Select(g => new { EggGradeId = g.Key, Quantity = g.Sum(x => x.Quantity) })
            .ToListAsync(ct);
        var gradeNames = await db.EggGrades
            .Where(g => gradeTotals.Select(t => t.EggGradeId).Contains(g.Id))
            .ToDictionaryAsync(g => g.Id, g => g.Name, ct);

        // Hen-days (spec §19.3): the farm's bird count on each day, summed over
        // the range. Count(D) = Σ flocks placed by D of (initial − removals ≤ D)
        // — computed as an event timeline: +initial at placement, −quantity at
        // each movement, baseline before `from`, then walked day by day.
        var placements = await db.Flocks
            .Select(f => new { Date = f.PlacementDate, Delta = (long)f.InitialCount })
            .ToListAsync(ct);
        var removals = await db.BirdMovements
            .GroupBy(m => m.Date)
            .Select(g => new { Date = g.Key, Delta = -g.Sum(m => (long)m.Quantity) })
            .ToListAsync(ct);
        var deltasByDate = placements.Concat(removals)
            .GroupBy(x => x.Date)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Delta));

        var baseline = deltasByDate.Where(kv => kv.Key < from).Sum(kv => kv.Value);

        var days = new List<ProductionDay>();
        var birds = baseline;
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            birds += deltasByDate.GetValueOrDefault(d);
            var henDays = Math.Max(birds, 0);
            var row = perDay.GetValueOrDefault(d);
            var total = row?.Total ?? 0;
            var sellable = total - (row?.Cracked ?? 0) - (row?.Dirty ?? 0) - (row?.Discarded ?? 0);
            days.Add(new ProductionDay(
                d, total, row?.Cracked ?? 0, row?.Dirty ?? 0, row?.Discarded ?? 0,
                sellable, row?.Deaths ?? 0, henDays,
                henDays > 0 ? Math.Round(total * 100m / henDays, 1) : null));
        }

        var totalEggs = days.Sum(x => x.TotalEggs);
        var totalHenDays = days.Sum(x => x.HenDays);
        return new ProductionReport(
            days,
            totalEggs,
            days.Sum(x => x.Sellable),
            days.Sum(x => x.Deaths),
            totalHenDays,
            totalHenDays > 0 ? Math.Round(totalEggs * 100m / totalHenDays, 1) : null,
            gradeTotals
                .Select(t => new GradeTotal(
                    t.EggGradeId, gradeNames.GetValueOrDefault(t.EggGradeId, "?"), t.Quantity))
                .OrderByDescending(t => t.Quantity)
                .ToList());
    }

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

        var revenue = confirmed.Sum(o => o.Total);
        var (code, minor) = await AccountCurrencyAsync(ct);
        return new SalesSummary(
            confirmed.Count, revenue, paid, revenue - paid, voidedCount, code, minor);
    }

    public async Task<ExpenseSummary> GetExpensesAsync(
        DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var perCategory = await db.Expenses
            .Where(e => e.Date >= from && e.Date <= to)
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
            .Where(e => e.Date >= from && e.Date <= to)
            .SumAsync(e => e.AmountMinorUnits, ct);

        var (code, minor) = await AccountCurrencyAsync(ct);
        return new ProfitReport(revenue, expenses, revenue - expenses, code, minor);
    }

    private async Task<(string Code, int Minor)> AccountCurrencyAsync(CancellationToken ct)
    {
        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(ct);
        return (account?.DefaultCurrencyCode ?? "", account?.DefaultCurrencyMinorUnit ?? 2);
    }
}
