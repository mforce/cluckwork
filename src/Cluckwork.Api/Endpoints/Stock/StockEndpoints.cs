namespace Cluckwork.Api.Endpoints.Stock;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.EggLots;
using Cluckwork.Infrastructure.Persistence;

public static class StockEndpoints
{
    public static RouteGroupBuilder MapStockEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", GetStock)
            .WithName("GetStock")
            .WithSummary("Current egg stock by grade. Available excludes withdrawal-restricted lots, which sum separately.");

        return group;
    }

    private static async Task<IResult> GetStock(
        IEggLotRepository eggLots, TenantContext tenant, IClock clock, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var rows = await eggLots.GetStockByGradeAsync(clock.TodayUtc, ct);
        return Results.Ok(rows.Select(r => new StockResponse(
            r.EggGradeId, r.GradeName, r.Available, r.Restricted)));
    }
}

public sealed record StockResponse(Guid EggGradeId, string GradeName, int Available, int Restricted);
