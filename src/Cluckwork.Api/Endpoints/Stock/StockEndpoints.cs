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

        // #101 — the ledger behind the cached balances. Production data:
        // open to any signed-in user, like the stock summary.
        group.MapGet("/lots", ListLots)
            .WithName("ListEggLots")
            .WithSummary("Egg lots newest-production-first (optional gradeId filter, paged).");

        group.MapGet("/lots/{id:guid}/movements", ListLotMovements)
            .WithName("ListEggLotMovements")
            .WithSummary("A lot's movement ledger, newest first — every change to its available quantity.");

        return group;
    }

    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    private static async Task<IResult> ListLots(
        IEggLotRepository eggLots, TenantContext tenant, CancellationToken ct,
        Guid? gradeId = null, int? limit = null, int? offset = null)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        var take = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);
        var skip = Math.Max(offset ?? 0, 0);
        var lots = await eggLots.ListAsync(gradeId, take, skip, ct);
        return Results.Ok(lots.Select(l => new EggLotResponse(
            l.Id, l.EggGradeId, l.ProductionDate, l.QuantityProduced,
            l.QuantityAvailable, l.RestrictedUntil, l.DailyEntryId)));
    }

    private static async Task<IResult> ListLotMovements(
        Guid id, IEggLotRepository eggLots,
        Cluckwork.Application.Features.Eggs.IEggInventoryMovementRepository movements,
        TenantContext tenant, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        // Foreign lots read as null through the tenant filter — same 404 as
        // nonexistent, no existence oracle.
        if (await eggLots.GetByIdAsync(id, ct) is null) return Results.NotFound();
        var list = await movements.ListByLotAsync(id, ct);
        return Results.Ok(list.Select(m => new EggMovementResponse(
            m.Id, m.MovementType.ToString(), m.QuantityDelta,
            m.ReferenceType, m.ReferenceId, m.Reason, m.CreatedAtUtc)));
    }

    private static async Task<IResult> GetStock(
        IEggLotRepository eggLots, TenantContext tenant, IClock clock, CancellationToken ct)
    {
        if (!tenant.IsResolved) return Results.Unauthorized();
        // TodayUtc: the restriction boundary should use the farm-local date
        // (accounts carry TimeZoneId), but the allocation path (ConfirmSale) also
        // uses UTC today — both convert together in the timezone follow-up issue
        // rather than diverging here.
        var rows = await eggLots.GetStockByGradeAsync(clock.TodayUtc, ct);
        return Results.Ok(rows.Select(r => new StockResponse(
            r.EggGradeId, r.GradeName, r.Available, r.Restricted)));
    }
}

public sealed record StockResponse(Guid EggGradeId, string GradeName, int Available, int Restricted);

public sealed record EggLotResponse(
    Guid Id, Guid EggGradeId, DateOnly ProductionDate, int QuantityProduced,
    int QuantityAvailable, DateOnly? RestrictedUntil, Guid? DailyEntryId);

public sealed record EggMovementResponse(
    Guid Id, string MovementType, int QuantityDelta,
    string ReferenceType, Guid ReferenceId, string? Reason, DateTimeOffset CreatedAtUtc);
