namespace Cluckwork.Api.Endpoints.Reports;

using Cluckwork.Application.Features.Reports;
using Cluckwork.Infrastructure.Persistence;

// #91 — core reports. Production is open to every signed-in user (workers
// record it, workers may read it); the money summaries are AdminOnly, reads
// included (#87/#89 split).
public static class ReportEndpoints
{
    // Guard: a report over decades of days would build a giant payload row by
    // row. One year covers every report the phase needs.
    private const int MaxRangeDays = 366;

    public static RouteGroupBuilder MapReportEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/production", Production)
            .WithName("ProductionReport")
            .WithSummary("Per-day production across the range: eggs, losses, sellable, deaths, hen-day % (spec §19.3), plus period totals and grade breakdown.");

        group.MapGet("/sales", Sales)
            .WithName("SalesSummaryReport")
            .WithSummary("Confirmed orders in the range: count, revenue, settled payments, outstanding; voided count.")
            .RequireAuthorization(AuthPolicies.AdminOnly);

        group.MapGet("/expenses", Expenses)
            .WithName("ExpenseSummaryReport")
            .WithSummary("Per-category expense totals and grand total for the range.")
            .RequireAuthorization(AuthPolicies.AdminOnly);

        group.MapGet("/profit", Profit)
            .WithName("ProfitReport")
            .WithSummary("Basic profit for the range: confirmed revenue minus recorded expenses (no COGS).")
            .RequireAuthorization(AuthPolicies.AdminOnly);

        return group;
    }

    private static IResult? ValidateRange(DateOnly? from, DateOnly? to, out DateOnly f, out DateOnly t)
    {
        // Default: the last 7 days (inclusive), UTC until #35.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        f = from ?? today.AddDays(-6);
        t = to ?? today;
        if (f > t)
            return Results.Problem("'from' must not be after 'to'.",
                statusCode: 400, title: "Report.InvalidRange");
        if (t.DayNumber - f.DayNumber >= MaxRangeDays)
            return Results.Problem($"Range cannot exceed {MaxRangeDays} days.",
                statusCode: 400, title: "Report.RangeTooLarge");
        // Reports describe the past; also guards the DateOnly.MaxValue walk
        // (codex review of #92).
        if (t > today)
            return Results.Problem("'to' cannot be in the future.",
                statusCode: 400, title: "Report.FutureRange");
        return null;
    }

    private static async Task<IResult> Production(
        IReportQueries reports, TenantContext tenant, CancellationToken ct,
        DateOnly? from = null, DateOnly? to = null)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var bad = ValidateRange(from, to, out var f, out var t);
        if (bad is not null) return bad;
        return Results.Ok(await reports.GetProductionAsync(f, t, ct));
    }

    private static async Task<IResult> Sales(
        IReportQueries reports, TenantContext tenant, CancellationToken ct,
        DateOnly? from = null, DateOnly? to = null)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var bad = ValidateRange(from, to, out var f, out var t);
        if (bad is not null) return bad;
        return Results.Ok(await reports.GetSalesAsync(f, t, ct));
    }

    private static async Task<IResult> Expenses(
        IReportQueries reports, TenantContext tenant, CancellationToken ct,
        DateOnly? from = null, DateOnly? to = null)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var bad = ValidateRange(from, to, out var f, out var t);
        if (bad is not null) return bad;
        return Results.Ok(await reports.GetExpensesAsync(f, t, ct));
    }

    private static async Task<IResult> Profit(
        IReportQueries reports, TenantContext tenant, CancellationToken ct,
        DateOnly? from = null, DateOnly? to = null)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var bad = ValidateRange(from, to, out var f, out var t);
        if (bad is not null) return bad;
        return Results.Ok(await reports.GetProfitAsync(f, t, ct));
    }
}
